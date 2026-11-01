using System.Collections.Concurrent;
using System.Threading.Channels;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services.Processing;

namespace EnrollmentService.Services;

// ============================================================================
// ПУНКТИ О1.3 (Пул обробників), О1.4 (Захист єдиного спільного ресурсу discipline_group),
// Д1.1 (Дві моделі виконання: async_tasks та dedicated_threads),
// Д1.2 (Незалежний періодичний семплер queue_length кожні 100 мс),
// Д1.4 (Коректне завершення роботи після SIGTERM: 503 для нових запитів, повне вивантаження черги)
// ============================================================================
public sealed class EnrollmentWorkerPool : IHostedService
{
    private readonly IBoundedRequestQueue _queue;
    private readonly EnrollmentRequestProcessor _processor;
    private readonly EnrollmentRequestRepository _requestRepo;
    private readonly ServiceOptions _options;
    private readonly MetricsCollector _metrics;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly List<Task> _asyncWorkers = new();
    private readonly List<Thread> _dedicatedThreads = new();
    private readonly CancellationTokenSource _workerEpochCts = new();
    private readonly CancellationTokenSource _samplerCts = new();
    private readonly ConcurrentDictionary<Guid, byte> _enqueuedItemIds = new();
    private readonly Channel<byte> _replenishSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private Task? _periodicSamplerTask;
    private Task? _replenishmentTask;
    private volatile bool _isShuttingDown;
    private int _shutdownInitiated;

    public bool IsShuttingDown => _isShuttingDown;

    public EnrollmentWorkerPool(
        IBoundedRequestQueue queue,
        EnrollmentRequestProcessor processor,
        EnrollmentRequestRepository requestRepo,
        ServiceOptions options,
        MetricsCollector metrics,
        IHostApplicationLifetime lifetime)
    {
        _queue = queue;
        _processor = processor;
        _requestRepo = requestRepo;
        _options = options;
        _metrics = metrics;
        _lifetime = lifetime;
    }

    public void TrackEnqueued(Guid id) => _enqueuedItemIds.TryAdd(id, 0);
    public void UntrackEnqueued(Guid id) => _enqueuedItemIds.TryRemove(id, out _);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableWorkers)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_pool_disabled", new Dictionary<string, object?>
            {
                ["detail"] = "Пул воркерів вимкнено для цього вузла (ENABLE_WORKERS=false)."
            });
            return;
        }

        // 1. Відновлення заявок у стані processing (визначення фінального стану done/failed)
        try
        {
            int recovered = await _requestRepo.RecoverStuckProcessingRequestsAsync(cancellationToken);
            if (recovered > 0)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "recovered_stuck_requests", new Dictionary<string, object?>
                {
                    ["recovered_count"] = recovered
                });
            }
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "recover_stuck_requests_failed", new Dictionary<string, object?>
            {
                ["error"] = ex.Message
            });
        }

        // 2. Відновлення збережених заявок у стані queued із PostgreSQL до оперативної черги
        try
        {
            int recoveredQueued = await ReplenishQueueFromDatabaseAsync(cancellationToken);
            if (recoveredQueued > 0)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "recovered_queued_requests", new Dictionary<string, object?>
                {
                    ["recovered_count"] = recoveredQueued,
                    ["current_queue_length"] = _queue.Count
                });
            }
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "recover_queued_requests_failed", new Dictionary<string, object?>
            {
                ["error"] = ex.Message
            });
        }

        _lifetime.ApplicationStopping.Register(() => InitiateShutdown());
        _periodicSamplerTask = Task.Run(() => RunIndependentQueueSamplerAsync(_samplerCts.Token));
        StartWorkers();
        _replenishmentTask = Task.Run(() => RunQueueReplenishmentLoopAsync(_workerEpochCts.Token));
    }

    // Послідовна дозавантаження заявок зі статусом queued із PostgreSQL у вільні слоти черги
    public async Task<int> ReplenishQueueFromDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_isShuttingDown) return 0;

        int enqueuedCount = 0;
        while (!_isShuttingDown && !cancellationToken.IsCancellationRequested)
        {
            int freeSlots = _queue.Capacity - _queue.Count;
            if (freeSlots <= 0)
            {
                break;
            }

            var pending = await _requestRepo.GetQueuedRequestsAsync(freeSlots, cancellationToken);
            if (pending.Count == 0)
            {
                break;
            }

            bool anyAddedThisBatch = false;
            foreach (var item in pending)
            {
                if (_isShuttingDown || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Захист від повторного додавання заявки, яка вже перебуває в черзі пам'яті
                if (!_enqueuedItemIds.TryAdd(item.Id, 0))
                {
                    continue;
                }

                if (!_queue.TryReserveSlot())
                {
                    _enqueuedItemIds.TryRemove(item.Id, out _);
                    break;
                }

                bool enqueued = _metrics.TryEnqueueReservedAndRecordAccepted(_queue, item);
                if (enqueued)
                {
                    enqueuedCount++;
                    anyAddedThisBatch = true;
                }
                else
                {
                    _queue.ReleaseSlot();
                    _enqueuedItemIds.TryRemove(item.Id, out _);
                    break;
                }
            }

            if (!anyAddedThisBatch)
            {
                break;
            }
        }

        return enqueuedCount;
    }

    private async Task RunQueueReplenishmentLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && !_isShuttingDown)
        {
            try
            {
                using var delayCts = new CancellationTokenSource(250);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, delayCts.Token);
                try
                {
                    await _replenishSignal.Reader.ReadAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                }

                if (token.IsCancellationRequested || _isShuttingDown)
                {
                    break;
                }

                if (_queue.Count < _queue.Capacity)
                {
                    await ReplenishQueueFromDatabaseAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "queue_replenishment_error", new Dictionary<string, object?>
                {
                    ["error"] = ex.Message
                });
                try { await Task.Delay(100, token); } catch { }
            }
        }
    }

    // Д1.2: Незалежне періодичне опитування довжини черги з фіксованим кроком 100 мс (поза подіями приходу/виходу)
    private async Task RunIndependentQueueSamplerAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                _metrics.RecordIndependentPeriodicSample(_queue.Count);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartWorkers()
    {
        var token = _workerEpochCts.Token;

        StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_pool_started", new Dictionary<string, object?>
        {
            ["workers"] = _options.Workers,
            ["execution_model"] = _options.ExecutionModel,
            ["queue_engine"] = _queue.EngineName,
            ["queue_capacity"] = _queue.Capacity,
            ["unsafe_mode"] = _options.UnsafeMode
        });

        if (string.Equals(_options.ExecutionModel, "dedicated_threads", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i <= _options.Workers; i++)
            {
                int workerId = i;
                var thread = new Thread(() => DedicatedThreadWorkerLoop(workerId, token))
                {
                    Name = $"DPC-Worker-Thread-{workerId}",
                    IsBackground = false
                };
                _dedicatedThreads.Add(thread);
                thread.Start();
            }
        }
        else
        {
            for (int i = 1; i <= _options.Workers; i++)
            {
                int workerId = i;
                _asyncWorkers.Add(Task.Run(() => AsyncTaskWorkerLoopAsync(workerId, token)));
            }
        }
    }

    // Д1.4: Перехоплення завершення — Kestrel продовжує приймати запити та відповідати 503 Service Unavailable,
    // поки активні задачі дообробляються з черги, після чого викликається StopApplication()
    public void InitiateShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownInitiated, 1) != 0)
        {
            return;
        }

        _isShuttingDown = true;
        _queue.Complete();

        StructuredJsonLogger.LogEvent(_options.InstanceId, "sigterm_received", new Dictionary<string, object?>
        {
            ["queue_length"] = _queue.Count,
            ["in_processing"] = _metrics.InProcessingCount,
            ["detail"] = "Отримано сигнал SIGTERM (Д1.4). Нові запити отримують 503 Service Unavailable; триває вивантаження прийнятої черги."
        });

        Task.Run(async () =>
        {
            await DrainAndStopAsync();
        });
    }

    private async Task DrainAndStopAsync()
    {
        try
        {
            if (_asyncWorkers.Count > 0)
            {
                await Task.WhenAll(_asyncWorkers);
            }

            if (_dedicatedThreads.Count > 0)
            {
                foreach (var thread in _dedicatedThreads)
                {
                    thread.Join();
                }
            }

            _workerEpochCts.Cancel();
            if (_replenishmentTask != null)
            {
                try { await _replenishmentTask; } catch { }
            }

            _samplerCts.Cancel();
            if (_periodicSamplerTask != null)
            {
                try { await _periodicSamplerTask; } catch { }
            }

            StructuredJsonLogger.LogEvent(_options.InstanceId, "graceful_shutdown_completed", new Dictionary<string, object?>
            {
                ["queue_length"] = _queue.Count,
                ["in_processing"] = _metrics.InProcessingCount,
                ["total_accepted"] = _metrics.TotalAccepted,
                ["total_done"] = _metrics.TotalDone,
                ["total_rejected"] = _metrics.TotalRejected,
                ["total_failed"] = _metrics.TotalFailed,
                ["detail"] = "Усі прийняті до SIGTERM запити успішно доведені до кінцевого стану. Журнал скинуто."
            });
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        InitiateShutdown();

        if (_asyncWorkers.Count > 0)
        {
            await Task.WhenAll(_asyncWorkers);
        }

        if (_dedicatedThreads.Count > 0)
        {
            foreach (var thread in _dedicatedThreads)
            {
                thread.Join();
            }
        }
    }

    private async Task AsyncTaskWorkerLoopAsync(int workerId, CancellationToken workerToken)
    {
        while (!workerToken.IsCancellationRequested)
        {
            QueuedRequestItem? item = null;
            try
            {
                item = await _queue.DequeueAsync(workerToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (item == null)
            {
                break;
            }

            try
            {
                await _processor.ProcessItemAsync(workerId, item, isAsyncModel: true, _queue, workerToken);
            }
            catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "async_worker_loop_exception", new Dictionary<string, object?>
                {
                    ["worker_id"] = workerId,
                    ["request_id"] = item.Id,
                    ["error"] = ex.Message
                });
            }
            finally
            {
                _enqueuedItemIds.TryRemove(item.Id, out _);
                _replenishSignal.Writer.TryWrite(1);
            }
        }
    }

    private void DedicatedThreadWorkerLoop(int workerId, CancellationToken workerToken)
    {
        while (!workerToken.IsCancellationRequested)
        {
            QueuedRequestItem? item = null;
            try
            {
                item = _queue.DequeueBlocking(workerToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (item == null)
            {
                break;
            }

            try
            {
                _processor.ProcessItemAsync(workerId, item, isAsyncModel: false, _queue, workerToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "dedicated_thread_loop_exception", new Dictionary<string, object?>
                {
                    ["worker_id"] = workerId,
                    ["request_id"] = item.Id,
                    ["error"] = ex.Message
                });
            }
            finally
            {
                _enqueuedItemIds.TryRemove(item.Id, out _);
                _replenishSignal.Writer.TryWrite(1);
            }
        }
    }
}
