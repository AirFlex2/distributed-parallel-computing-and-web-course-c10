using System.Collections.Concurrent;
using EnrollmentService.Domain;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using WebEnrollmentService = global::EnrollmentService.Services.EnrollmentService;

namespace EnrollmentService.Services.Processing;

public sealed class EnrollmentRequestProcessor : IEnrollmentRequestProcessor
{
    private readonly EnrollmentRequestRepository _requestRepo;
    private readonly GroupRepository _groupRepo;
    private readonly EnrollmentRepository _enrollmentRepo;
    private readonly DecanateRepository _decanateRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly MetricsCollector _metrics;
    private readonly ServiceOptions _options;
    private readonly IWebEnrollmentNotifier _webNotifier;
    private readonly RayEffectReceiverService _receiverService;
    private readonly IRayTaskClient _rayTaskClient;

    public EnrollmentRequestProcessor(
        EnrollmentRequestRepository requestRepo,
        GroupRepository groupRepo,
        EnrollmentRepository enrollmentRepo,
        DecanateRepository decanateRepo,
        IDbConnectionFactory connectionFactory,
        MetricsCollector metrics,
        ServiceOptions options,
        IWebEnrollmentNotifier? webNotifier = null,
        RayEffectReceiverService? receiverService = null,
        IRayTaskClient? rayTaskClient = null)
    {
        _requestRepo = requestRepo;
        _groupRepo = groupRepo;
        _enrollmentRepo = enrollmentRepo;
        _decanateRepo = decanateRepo;
        _connectionFactory = connectionFactory;
        _metrics = metrics;
        _options = options;
        _webNotifier = webNotifier ?? new WebEnrollmentNotifier(
            new WebEnrollmentService(groupRepo, enrollmentRepo, decanateRepo, connectionFactory, options));
        _receiverService = receiverService ?? new RayEffectReceiverService(
            requestRepo, groupRepo, enrollmentRepo, decanateRepo, connectionFactory, metrics, options, _webNotifier);
        _rayTaskClient = rayTaskClient ?? new RayTaskClient(options, _receiverService);
    }

    private readonly ConcurrentDictionary<Guid, Func<QueuedRequestItem, Task>> _scopedPreProcessingHooks = new();

    /// <summary>
    /// Реєструє тестову точку перехоплення, ізольовану виключно для конкретного ідентифікатора заявки (requestId).
    /// Запобігає взаємному впливу тестів та гарантує, що інші запити черги оброблятимуться без перешкод.
    /// Використовує TryAdd для запобігання непомітному перезапису та безпечне видалення за парою ключ-значення при Dispose.
    /// Повертає IDisposable для надійного автоматичного видалення реєстрації.
    /// </summary>
    public IDisposable RegisterPreProcessingHook(Guid requestId, Func<QueuedRequestItem, Task> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        if (!_scopedPreProcessingHooks.TryAdd(requestId, hook))
        {
            throw new InvalidOperationException($"Хук перехоплення для requestId '{requestId}' вже зареєстровано.");
        }

        return new ScopedHookRegistration(() =>
        {
            _scopedPreProcessingHooks.TryRemove(new KeyValuePair<Guid, Func<QueuedRequestItem, Task>>(requestId, hook));
        });
    }

    /// <summary>
    /// Реєструє тестову точку перехоплення після успішного COMMIT транзакції DPC (де запис уже внесено до decanate_roster),
    /// але ДО надсилання сповіщення Web-контуру через IWebEnrollmentNotifier.
    /// Дозволяє детерміновано протестувати стан гонки між фіксацією у відомості та скасуванням Web-запису.
    /// </summary>
    public IDisposable RegisterPostCommitBeforeNotificationHook(Guid requestId, Func<QueuedRequestItem, Task> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        if (!_receiverService.ScopedPostCommitBeforeNotifyHooks.TryAdd(requestId, hook))
        {
            throw new InvalidOperationException($"Хук після коміту для requestId '{requestId}' вже зареєстровано.");
        }

        return new ScopedHookRegistration(() =>
        {
            _receiverService.ScopedPostCommitBeforeNotifyHooks.TryRemove(
                new KeyValuePair<Guid, Func<QueuedRequestItem, Task>>(requestId, hook));
        });
    }

    private sealed class ScopedHookRegistration : IDisposable
    {
        private Action? _disposeAction;
        public ScopedHookRegistration(Action disposeAction) => _disposeAction = disposeAction;
        public void Dispose() => Interlocked.Exchange(ref _disposeAction, null)?.Invoke();
    }

    /// <summary>
    /// Тестова точка перехоплення, що викликається детерміновано одразу після COMMIT бізнес-транзакції,
    /// але до виходу з методу обробки (для перевірки відсутності гонки публікації FinishProcessingTimestamp).
    /// </summary>
    public Func<QueuedRequestItem, Task>? TestHookAfterCommitAsync
    {
        get => _receiverService.TestHookAfterCommitAsync;
        set => _receiverService.TestHookAfterCommitAsync = value;
    }

    /// <summary>
    /// Глобальна тестова точка перехоплення перед початком обробки заявки воркером (для детермінованого тестування черги та ідемпотентності).
    /// </summary>
    public Func<QueuedRequestItem, Task>? TestPreProcessingHookAsync { get; set; }

    public async Task ProcessItemAsync(
        int workerId,
        QueuedRequestItem item,
        bool isAsyncModel,
        IBoundedRequestQueue queue,
        CancellationToken cancellationToken = default)
    {
        // Детермінована ізольована точка перехоплення перед стартом обробки
        if (_scopedPreProcessingHooks.TryGetValue(item.Id, out var scopedHook))
        {
            try
            {
                await scopedHook(item);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_scoped_hook_failed", new Dictionary<string, object?>
                {
                    ["worker_id"] = workerId,
                    ["request_id"] = item.Id,
                    ["error"] = ex.Message
                });
            }
        }

        if (_options.AllowTestOverrides && TestPreProcessingHookAsync != null)
        {
            try
            {
                await TestPreProcessingHookAsync(item);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_preprocessing_hook_failed", new Dictionary<string, object?>
                {
                    ["worker_id"] = workerId,
                    ["request_id"] = item.Id,
                    ["error"] = ex.Message
                });
            }
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        bool started = false;

        try
        {
            started = await _requestRepo.TryMarkStartedAsync(item.Id, startedAtUtc, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _metrics.RecordWorkerFinished(item, "failed", queue.Count);
            throw;
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_mark_started_failed", new Dictionary<string, object?>
            {
                ["worker_id"] = workerId,
                ["request_id"] = item.Id,
                ["error"] = ex.Message
            });
        }

        if (!started)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_request_already_claimed", new Dictionary<string, object?>
            {
                ["worker_id"] = workerId,
                ["request_id"] = item.Id,
                ["detail"] = "Заявку вже було взято в обробку іншим воркером або завершено під час відновлення."
            });
            return;
        }

        _metrics.RecordWorkerStarted(item, queue.Count);

        string finalStatus = "failed";
        string? rejectionReason = null;
        bool committed = false;

        try
        {
            bool isUnsafe = _options.AllowTestOverrides && _options.UnsafeMode;
            // РПО ЛР3 (О3.1, О3.3, О3.4): Виконання задачі в Ray + атомарне застосування ефекту приймачем
            (finalStatus, rejectionReason, committed) = await _rayTaskClient.ExecuteAsync(item, isUnsafe, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!committed)
            {
                finalStatus = "failed";
                rejectionReason = "Обробку перервано через зупинку процесу (SIGTERM/Cancellation).";
            }
        }
        catch (Exception ex)
        {
            if (!committed)
            {
                finalStatus = "failed";
                rejectionReason = $"Системна помилка під час обробки: {ex.Message}";
            }
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_processing_exception", new Dictionary<string, object?>
            {
                ["worker_id"] = workerId,
                ["request_id"] = item.Id,
                ["error"] = ex.ToString()
            });
        }

        try
        {
            var finishedAtUtc = _metrics.MarkProcessingFinished(item);

            if (!committed && finalStatus != "done")
            {
                bool updated = false;
                for (int attempt = 1; attempt <= 3 && !updated; attempt++)
                {
                    try
                    {
                        await using var conn = await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
                        if (finalStatus == "failed")
                        {
                            var appliedEffect = await _requestRepo.GetAppliedEffectDecisionAsync(
                                item.Id, conn, CancellationToken.None);
                            if (appliedEffect != null && appliedEffect.Status is "done" or "rejected")
                            {
                                finalStatus = appliedEffect.Status;
                                rejectionReason = appliedEffect.RejectionReason;
                                committed = string.Equals(appliedEffect.Status, "done", StringComparison.Ordinal);

                                await _requestRepo.UpdateStatusAsync(
                                    item.Id,
                                    appliedEffect.Status,
                                    appliedEffect.RejectionReason,
                                    appliedEffect.AssignedGroupId,
                                    finishedAtUtc,
                                    conn,
                                    tx: null,
                                    CancellationToken.None);

                                StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_reconciled_from_applied_effects", new Dictionary<string, object?>
                                {
                                    ["worker_id"] = workerId,
                                    ["request_id"] = item.Id,
                                    ["status"] = finalStatus
                                });

                                updated = true;
                                break;
                            }
                        }

                        await _requestRepo.UpdateStatusAsync(
                            item.Id,
                            finalStatus,
                            rejectionReason,
                            null,
                            finishedAtUtc,
                            conn,
                            tx: null,
                            CancellationToken.None);
                        updated = true;
                    }
                    catch (Exception ex)
                    {
                        StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_status_retry_failed", new Dictionary<string, object?>
                        {
                            ["request_id"] = item.Id,
                            ["attempt"] = attempt,
                            ["error"] = ex.Message
                        });
                        await Task.Delay(50 * attempt);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_status_update_failed", new Dictionary<string, object?>
            {
                ["request_id"] = item.Id,
                ["error"] = ex.Message
            });
        }
        finally
        {
            _metrics.RecordWorkerFinished(item, finalStatus, queue.Count);
        }

        StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_processed_item", new Dictionary<string, object?>
        {
            ["worker_id"] = workerId,
            ["model"] = isAsyncModel ? "async_tasks" : "dedicated_threads",
            ["request_id"] = item.Id,
            ["student_id"] = item.StudentId,
            ["group_id"] = item.GroupId,
            ["status"] = finalStatus,
            ["rejection_reason"] = rejectionReason
        });
    }
}
