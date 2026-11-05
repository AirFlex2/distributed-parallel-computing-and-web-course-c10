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
    private readonly WebEnrollmentService _enrollmentService;

    public EnrollmentRequestProcessor(
        EnrollmentRequestRepository requestRepo,
        GroupRepository groupRepo,
        EnrollmentRepository enrollmentRepo,
        DecanateRepository decanateRepo,
        IDbConnectionFactory connectionFactory,
        MetricsCollector metrics,
        ServiceOptions options,
        WebEnrollmentService? enrollmentService = null)
    {
        _requestRepo = requestRepo;
        _groupRepo = groupRepo;
        _enrollmentRepo = enrollmentRepo;
        _decanateRepo = decanateRepo;
        _connectionFactory = connectionFactory;
        _metrics = metrics;
        _options = options;
        _enrollmentService = enrollmentService ?? new WebEnrollmentService(groupRepo, enrollmentRepo, decanateRepo, connectionFactory, options);
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
    public Func<QueuedRequestItem, Task>? TestHookAfterCommitAsync { get; set; }

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
            // Єдиний конвеєр обробки для SAFE та UNSAFE режимів (О1.4 РПО):
            // Єдина відмінність між ними — наявність транзакційного блокування FOR UPDATE у SAFE.
            (finalStatus, rejectionReason, committed) = await ProcessEnrollmentCoreAsync(item, isUnsafe, cancellationToken);
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

            // Якщо це не done або транзакція не була зафіксована (наприклад, rejected або failed до коміту),
            // записуємо фінальний статус у БД.
            // (Для статусу 'done' у SAFE та UNSAFE режимах він вже зафіксований атомарно всередині tx)
            if (!committed && finalStatus != "done")
            {
                bool updated = false;
                for (int attempt = 1; attempt <= 3 && !updated; attempt++)
                {
                    try
                    {
                        await using var conn = await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
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

    private async Task<(string Status, string? Reason, bool Committed)> ProcessEnrollmentCoreAsync(
        QueuedRequestItem item,
        bool isUnsafe,
        CancellationToken cancellationToken)
    {
        // Симуляція повільного виклику деканату (200–700 мс) виконується
        // СТРОГО ПОЗА критичною секцією (до відкриття транзакції та взяття блокувань БД)
        // в обох режимах (SAFE та UNSAFE, О1.3 РПО).
        // В UNSAFE фіксована затримка (SlowCallMinMs = 200 мс) забезпечує одночасний вхід
        // паралельних воркерів у транзакцію для надійної демонстрації Lost Update без блокування FOR UPDATE.
        int slowDelayMs = isUnsafe
            ? _options.SlowCallMinMs
            : Random.Shared.Next(_options.SlowCallMinMs, _options.SlowCallMaxMs + 1);
        await Task.Delay(slowDelayMs, cancellationToken);

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        bool committed = false;

        try
        {
            // Канонічний порядок блокувань для виключення взаємоблокувань (deadlocks):
            // 1. student FOR UPDATE (у SAFE режимі) або звичайний SELECT (в UNSAFE)
            var student = isUnsafe
                ? await _enrollmentRepo.GetStudentByIdAsync(item.StudentId, cancellationToken)
                : await _enrollmentRepo.GetStudentForUpdateAsync(item.StudentId, conn, tx, cancellationToken);

            if (student == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ("rejected", $"Студента '{item.StudentId}' не знайдено в базі даних.", false);
            }

            // 2. discipline_group FOR UPDATE (у SAFE режимі) або звичайний SELECT (в UNSAFE)
            var groupWithDisc = await _groupRepo.GetGroupWithDisciplineAsync(
                item.GroupId,
                conn,
                tx,
                forUpdate: !isUnsafe,
                cancellationToken);

            if (!groupWithDisc.HasValue)
            {
                await tx.RollbackAsync(cancellationToken);
                return ("failed", $"Навчальну групу '{item.GroupId}' не знайдено.", false);
            }

            var group = groupWithDisc.Value.Group;
            Guid? targetEnrollmentId = item.RequestedEnrollmentId ?? item.EnrollmentId;

            if (!group.DpcSandboxEnabled && !targetEnrollmentId.HasValue)
            {
                await tx.RollbackAsync(cancellationToken);
                return ("rejected", $"Група '{item.GroupId}' не призначена для автономної обробки РПО (dpc_sandbox_enabled = false).", false);
            }

            // У режимі UNSAFE спільний наскрізний сценарій Web + DPC суворо заборонено,
            // оскільки UNSAFE призначений виключно для лабораторної демонстрації Lost Update на автономному прийомі
            if (isUnsafe && targetEnrollmentId.HasValue)
            {
                await tx.RollbackAsync(cancellationToken);
                return ("rejected", "Спільний наскрізний сценарій Web + DPC підтримується виключно в безпечному режимі SAFE (атомарна транзакція з блокуванням FOR UPDATE).", false);
            }

            string disciplineId = group.DisciplineId;
            decimal disciplineEcts = groupWithDisc.Value.DisciplineEcts;

            // 3. Перевірка ідемпотентності за request_id
            string? existingRosterGroup = await _decanateRepo.GetRosterGroupForRequestAsync(item.Id, conn, tx, cancellationToken);
            if (existingRosterGroup != null)
            {
                // О1.5 DPC: час завершення обробки публікується ДО COMMIT, щоб будь-який конкурентний GET
                // гарантовано зафіксував delivery_wait_ms на найпершому отриманні фінального результату
                var finishedAtUtc = _metrics.MarkProcessingFinished(item);
                await _requestRepo.UpdateStatusAsync(
                    item.Id,
                    "done",
                    null,
                    existingRosterGroup,
                    finishedAtUtc,
                    conn,
                    tx,
                    cancellationToken);
                await tx.CommitAsync(cancellationToken);
                committed = true;

                if (_options.AllowTestOverrides && TestHookAfterCommitAsync != null)
                {
                    await TestHookAfterCommitAsync(item);
                }

                return ("done", null, true);
            }

            if (!isUnsafe)
            {
                // Спільний наскрізний сценарій Web + DPC (тема C10, §2.6.4):
                // Якщо передано requested_enrollment_id (або enrollment_id), місце вже зарезервовано Web-контуром (held).
                // Воркер РПО в безпечному режимі (SAFE) перевіряє повну валідність зв'язування, підтверджує запис (held -> confirmed)
                // та вносить його до відомості decanate_roster без повторного списання available або кредитів студента (Сценарій Б).
                if (targetEnrollmentId.HasValue)
                {
                    // 1. Перевіряємо існування та блокуємо запис enrollment FOR UPDATE
                    var heldEnrollment = await _enrollmentRepo.GetEnrollmentForJointVerificationAsync(
                        targetEnrollmentId.Value, conn, tx, cancellationToken);

                    if (!heldEnrollment.HasValue)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return ("rejected", $"Пов'язаний Web-запис '{targetEnrollmentId.Value}' не знайдено або було видалено з бази даних.", false);
                    }

                    // 2. Перевіряємо відповідність студента
                    if (!string.Equals(heldEnrollment.Value.StudentId, item.StudentId, StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return ("rejected", $"Web-запис '{targetEnrollmentId.Value}' належить іншому студенту ('{heldEnrollment.Value.StudentId}', очікувався '{item.StudentId}').", false);
                    }

                    // 3. Перевіряємо відповідність групи
                    if (!string.Equals(heldEnrollment.Value.GroupId, item.GroupId, StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return ("rejected", $"Web-запис '{targetEnrollmentId.Value}' відноситься до групи '{heldEnrollment.Value.GroupId}', а не '{item.GroupId}'.", false);
                    }

                    // 4. Перевіряємо статус (має бути 'held')
                    if (!string.Equals(heldEnrollment.Value.Status, "held", StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return ("rejected", $"Web-запис '{targetEnrollmentId.Value}' перебуває у неприпустимому для підтвердження стані '{heldEnrollment.Value.Status}'.", false);
                    }

                    // 5. Перевіряємо строк дії резервування (held)
                    if (heldEnrollment.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return ("rejected", $"Термін дії резервування Web-запису '{targetEnrollmentId.Value}' сплив.", false);
                    }

                    // 6. Перевіряємо чи група не закрита деканатом
                    if (group.Closed)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return ("rejected", $"Навчальна група '{group.Id}' закрита працівником деканату (closed = true).", false);
                    }

                    var finishedAtUtcJoint = _metrics.MarkProcessingFinished(item);

                    try
                    {
                        // РПО вносить запис до відомості decanate_roster у спільній транзакції
                        await _decanateRepo.InsertRosterEntryAsync(
                            item.Id,
                            item.StudentId,
                            item.GroupId,
                            disciplineId,
                            disciplineEcts,
                            targetEnrollmentId.Value,
                            "joint_web_enrollment",
                            finishedAtUtcJoint,
                            conn,
                            tx,
                            cancellationToken);
                    }
                    catch (DecanateConflictException ex)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return ("rejected", ex.Message, false);
                    }

                    // Web підтверджує Web-запис (held -> confirmed) в єдиній спільній транзакції
                    bool confirmed = await _enrollmentService.ConfirmEnrollmentFromRosterAsync(
                        targetEnrollmentId.Value, item.Id, conn, tx, cancellationToken);
                    if (!confirmed)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return ("rejected", $"Web-запис '{targetEnrollmentId.Value}' не вдалося підтвердити у спільній транзакції.", false);
                    }

                    await _requestRepo.UpdateStatusAsync(
                        item.Id,
                        "done",
                        null,
                        item.GroupId,
                        finishedAtUtcJoint,
                        conn,
                        tx,
                        cancellationToken);

                    await tx.CommitAsync(cancellationToken);
                    committed = true;

                    if (_options.AllowTestOverrides && TestHookAfterCommitAsync != null)
                    {
                        await TestHookAfterCommitAsync(item);
                    }

                    return ("done", null, true);
                }

                // 4. Перевірка наявності активного запису на дисципліну в обох таблицях для автономного прийому (єдине предметне правило §1.10)
                bool alreadyEnrolled = await _enrollmentRepo.HasActiveEnrollmentForDisciplineAsync(item.StudentId, disciplineId, conn, tx, cancellationToken);

                var policyResult = EnrollmentPolicy.Check(
                    student,
                    group,
                    disciplineEcts,
                    DateTimeOffset.UtcNow,
                    alreadyEnrolled);

                if (!policyResult.Allowed)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return ("rejected", policyResult.RejectionReason, false);
                }

                // БЕЗПЕЧНИЙ РЕЖИМ (SAFE) — Сценарій А (Автономний стенд РПО ЛР1):
                // Пряма паралельна обробка заявок черги з атомарним декрементом available під FOR UPDATE.
                // (У цільовому Сценарії Б за подіями Web на ЛР4 первинне списання здійснює Web, а РПО веде відомість, а перерозподіл second_choice заплановано у ЛР6 (координація виконавців та захист спільного ресурсу поколінням)).
                bool decremented = await _groupRepo.DecrementAvailableAsync(item.GroupId, conn, tx, cancellationToken);
                if (!decremented)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return ("rejected", $"У навчальній групі '{item.GroupId}' немає вільних місць (декремент available повернув false).", false);
                }

                // Оновлення кількості та кредитів студента
                await _enrollmentRepo.UpdateStudentStatsAsync(item.StudentId, +1, disciplineEcts, conn, tx, cancellationToken);

                // О1.5 DPC: час завершення обробки публікується ДО COMMIT, щоб будь-який конкурентний GET
                // гарантовано зафіксував delivery_wait_ms на найпершому отриманні фінального результату
                var finishedAtUtc = _metrics.MarkProcessingFinished(item);

                try
                {
                    // Запис до відомості деканату
                    await _decanateRepo.InsertRosterEntryAsync(
                        item.Id,
                        item.StudentId,
                        item.GroupId,
                        disciplineId,
                        disciplineEcts,
                        null,
                        "primary_roster",
                        finishedAtUtc,
                        conn,
                        tx,
                        cancellationToken);
                }
                catch (DecanateConflictException ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return ("rejected", ex.Message, false);
                }

                // Запис кінцевого стану 'done' у тій самій транзакції, що і предметний ефект
                await _requestRepo.UpdateStatusAsync(
                    item.Id,
                    "done",
                    null,
                    item.GroupId,
                    finishedAtUtc,
                    conn,
                    tx,
                    cancellationToken);

                await tx.CommitAsync(cancellationToken);
                committed = true;

                if (_options.AllowTestOverrides && TestHookAfterCommitAsync != null)
                {
                    await TestHookAfterCommitAsync(item);
                }

                return ("done", null, true);
            }
            else
            {
                // НЕБЕЗПЕЧНИЙ РЕЖИМ (UNSAFE): автономний прийом без транзакційного блокування FOR UPDATE.
                bool alreadyEnrolled = await _enrollmentRepo.HasActiveEnrollmentForDisciplineAsync(item.StudentId, disciplineId, conn, tx, cancellationToken);

                var policyResult = EnrollmentPolicy.Check(
                    student,
                    group,
                    disciplineEcts,
                    DateTimeOffset.UtcNow,
                    alreadyEnrolled);

                if (!policyResult.Allowed)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return ("rejected", policyResult.RejectionReason, false);
                }

                // Прочитали available, потім записали available - 1 без умови WHERE available > 0 (Lost Update).
                int newAvailable = group.Available - 1;
                await _groupRepo.SetAvailableUnsafeAsync(item.GroupId, newAvailable, conn, tx, cancellationToken);

                await _enrollmentRepo.UpdateStudentStatsAsync(item.StudentId, +1, disciplineEcts, conn, tx, cancellationToken);

                var finishedAtUtc = _metrics.MarkProcessingFinished(item);
                try
                {
                    await _decanateRepo.InsertRosterEntryAsync(
                        item.Id,
                        item.StudentId,
                        item.GroupId,
                        disciplineId,
                        disciplineEcts,
                        null,
                        "unsafe_overcommit",
                        finishedAtUtc,
                        conn,
                        tx,
                        cancellationToken);
                }
                catch (DecanateConflictException ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return ("rejected", ex.Message, false);
                }

                await _requestRepo.UpdateStatusAsync(
                    item.Id,
                    "done",
                    null,
                    item.GroupId,
                    finishedAtUtc,
                    conn,
                    tx,
                    cancellationToken);

                await tx.CommitAsync(cancellationToken);
                committed = true;

                if (_options.AllowTestOverrides && TestHookAfterCommitAsync != null)
                {
                    await TestHookAfterCommitAsync(item);
                }

                return ("done", null, true);
            }
        }
        catch
        {
            if (!committed)
            {
                _metrics.ResetProcessingFinished(item);
                try
                {
                    await tx.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Транзакція могла вже бути зафіксована до виникнення помилки
                }
            }
            throw;
        }
    }
}
