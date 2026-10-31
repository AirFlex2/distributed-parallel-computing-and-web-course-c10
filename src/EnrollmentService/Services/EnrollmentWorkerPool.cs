using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Services.Processing;

namespace EnrollmentService.Services;

// ============================================================================
// ПУНКТИ О1.3 (Пул обробників), О1.4 (Захист єдиного спільного ресурсу discipline_group),
// Д1.1 (Дві моделі виконання: async_tasks та dedicated_threads),
// Д1.2 (Незалежний періодичний семплер queue_length кожні 100 мс),
// Д1.4 (Коректне завершення роботи після SIGTERM зі структурованим JSON-журналом у stdout)
// ============================================================================
public sealed class EnrollmentWorkerPool : IHostedService
{
    private readonly IBoundedRequestQueue _queue;
    private readonly IEnrollmentRequestProcessor _processor;
    private readonly ServiceOptions _options;
    private readonly MetricsCollector _metrics;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly List<Task> _asyncWorkers = new();
    private readonly List<Thread> _dedicatedThreads = new();
    private CancellationTokenSource _workerEpochCts = new();
    private CancellationTokenSource _samplerCts = new();
    private Task? _periodicSamplerTask;
    private volatile bool _isShuttingDown;
    private int _sigtermLogged;

    public bool IsShuttingDown => _isShuttingDown;

    public EnrollmentWorkerPool(
        IBoundedRequestQueue queue,
        IEnrollmentRequestProcessor processor,
        ServiceOptions options,
        MetricsCollector metrics,
        IHostApplicationLifetime lifetime)
    {
        _queue = queue;
        _processor = processor;
        _options = options;
        _metrics = metrics;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStopping.Register(OnApplicationStopping);
        _periodicSamplerTask = Task.Run(() => RunIndependentQueueSamplerAsync(_samplerCts.Token));
        StartWorkersForCurrentModel();
        return Task.CompletedTask;
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

    private void StartWorkersForCurrentModel()
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

    public async Task SwitchExecutionModelAsync(string executionModel)
    {
        if (string.Equals(_options.ExecutionModel, executionModel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _workerEpochCts.Cancel();

        if (_asyncWorkers.Count > 0)
        {
            try { await Task.WhenAll(_asyncWorkers); } catch { }
            _asyncWorkers.Clear();
        }

        if (_dedicatedThreads.Count > 0)
        {
            foreach (var thread in _dedicatedThreads)
            {
                thread.Join();
            }
            _dedicatedThreads.Clear();
        }

        _workerEpochCts.Dispose();
        _workerEpochCts = new CancellationTokenSource();
        _options.ExecutionModel = executionModel;

        StartWorkersForCurrentModel();
    }

    // Д1.4: Обробник сигналу SIGTERM — обов'язково записує структурований однорядковий JSON у stdout
    private void OnApplicationStopping()
    {
        _isShuttingDown = true;
        _queue.Complete();

        if (Interlocked.Exchange(ref _sigtermLogged, 1) == 0)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "sigterm_received", new Dictionary<string, object?>
            {
                ["queue_length"] = _queue.Count,
                ["in_processing"] = _metrics.InProcessingCount,
                ["detail"] = "Отримано сигнал SIGTERM (Д1.4). Нові запити отримують 503 Service Unavailable; триває вивантаження прийнятої черги."
            });
        }
    }

    // Д1.4: Очікування повного спустошення черги, завершення всіх активних воркерів і скидання фінального JSON-запису
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        OnApplicationStopping();

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

    private async Task AsyncTaskWorkerLoopAsync(int workerId, CancellationToken workerToken)
    {
        while (!workerToken.IsCancellationRequested)
        {
            var item = await _queue.DequeueAsync(workerToken);
            if (item == null)
            {
                break;
            }

            await _processor.ProcessItemAsync(workerId, item, isAsyncModel: true, _queue, workerToken);
        }
    }

    private void DedicatedThreadWorkerLoop(int workerId, CancellationToken workerToken)
    {
        while (!workerToken.IsCancellationRequested)
        {
            var item = _queue.DequeueBlocking(workerToken);
            if (item == null)
            {
                break;
            }

            _processor.ProcessItemAsync(workerId, item, isAsyncModel: false, _queue, workerToken).GetAwaiter().GetResult();
        }
    }
}
