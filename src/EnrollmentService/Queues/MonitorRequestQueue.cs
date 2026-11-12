namespace EnrollmentService.Queues;

// ============================================================================
// ПУНКТ Д1.3: Власна обмежена черга на м'ютексі (lock) та умовній змінній (Monitor.Wait / Monitor.PulseAll)
// Для асинхронної моделі (Д1.1 async_tasks) використовує чергу TaskCompletionSource під тим самим м'ютексом,
// не блокуючи жодного потоку ThreadPool під час очікування нових заявок.
// ============================================================================
public sealed class MonitorRequestQueue : IBoundedRequestQueue
{
    private readonly Queue<QueuedRequestItem> _queue;
    private readonly Queue<TaskCompletionSource<bool>> _asyncPulseWaiters = new();
    private readonly object _syncRoot = new();
    private bool _isCompleted;
    private int _reservedSlots;

    public int Capacity { get; }
    public string EngineName => "custom_monitor (Monitor.Wait + async TaskCompletionSource)";

    public MonitorRequestQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _queue = new Queue<QueuedRequestItem>(capacity);
    }

    public int Count
    {
        get
        {
            lock (_syncRoot)
            {
                return _queue.Count;
            }
        }
    }

    public bool TryReserveSlot()
    {
        lock (_syncRoot)
        {
            if (_isCompleted || (_queue.Count + _reservedSlots) >= Capacity)
            {
                return false;
            }
            _reservedSlots++;
            return true;
        }
    }

    public void ReleaseSlot()
    {
        lock (_syncRoot)
        {
            if (_reservedSlots > 0)
            {
                _reservedSlots--;
            }
        }
    }

    public bool EnqueueReserved(QueuedRequestItem item)
    {
        lock (_syncRoot)
        {
            if (_isCompleted)
            {
                return false;
            }

            if (_reservedSlots > 0)
            {
                _reservedSlots--;
            }

            _queue.Enqueue(item);

            // Пробудження очікуючого асинхронного воркера (async_tasks)
            while (_asyncPulseWaiters.Count > 0)
            {
                var waiter = _asyncPulseWaiters.Dequeue();
                if (waiter.TrySetResult(true))
                {
                    break;
                }
            }

            // Пробудження очікуючого блокуючого потоку (dedicated_threads через Monitor.Pulse)
            Monitor.Pulse(_syncRoot);
            return true;
        }
    }

    public bool TryEnqueue(QueuedRequestItem item)
    {
        lock (_syncRoot)
        {
            if (_isCompleted || (_queue.Count + _reservedSlots) >= Capacity)
            {
                return false;
            }

            _queue.Enqueue(item);

            while (_asyncPulseWaiters.Count > 0)
            {
                var waiter = _asyncPulseWaiters.Dequeue();
                if (waiter.TrySetResult(true))
                {
                    break;
                }
            }

            Monitor.Pulse(_syncRoot);
            return true;
        }
    }

    public async ValueTask<QueuedRequestItem?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TaskCompletionSource<bool>? waiter = null;

            lock (_syncRoot)
            {
                if (_queue.Count > 0)
                {
                    return _queue.Dequeue();
                }

                if (_isCompleted)
                {
                    return null;
                }

                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _asyncPulseWaiters.Enqueue(waiter);
            }

            using var reg = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));

            try
            {
                await waiter.Task;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    public QueuedRequestItem? DequeueBlocking(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() =>
        {
            lock (_syncRoot)
            {
                Monitor.PulseAll(_syncRoot);
            }
        });

        lock (_syncRoot)
        {
            while (_queue.Count == 0 && !_isCompleted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                // Пасивне очікування на умовній змінній без таймауту (Д1.3): пробуджується Pulse/PulseAll або cancellation callback
                Monitor.Wait(_syncRoot);
            }

            if (_queue.Count > 0)
            {
                return _queue.Dequeue();
            }

            return null;
        }
    }

    public void Complete()
    {
        lock (_syncRoot)
        {
            _isCompleted = true;
            while (_asyncPulseWaiters.Count > 0)
            {
                _asyncPulseWaiters.Dequeue().TrySetResult(true);
            }
            Monitor.PulseAll(_syncRoot);
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _queue.Clear();
        }
    }
}
