using System.Threading.Channels;

namespace EnrollmentService.Queues;

// Уніфікований інтерфейс обмеженої черги (О1.2, Д1.3 РПО)
public interface IBoundedRequestQueue
{
    int Capacity { get; }
    int Count { get; }
    string EngineName { get; }

    // Неблокуюча спроба додати елемент. Повертає false, якщо черга заповнена (для відмови 429 у О1.2)
    bool TryEnqueue(QueuedRequestItem item);

    // Справжнє неблокуюче асинхронне отримання елемента без блокування потоків ThreadPool (для моделі async_tasks у Д1.1)
    ValueTask<QueuedRequestItem?> DequeueAsync(CancellationToken cancellationToken);

    // Синхронне блокуюче отримання елемента на Monitor.Wait (для моделі dedicated_threads у Д1.1 та Д1.3)
    QueuedRequestItem? DequeueBlocking(CancellationToken cancellationToken);

    // Сигнал про припинення приймання нових елементів (для коректного завершення Д1.4)
    void Complete();

    // Очищення черги для скидання стану стенду між дослідами
    void Clear();
}

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

    // О1.2 та Д1.3: Захищене м'ютексом додавання з перевіркою межі QUEUE_CAPACITY
    public bool TryEnqueue(QueuedRequestItem item)
    {
        lock (_syncRoot)
        {
            if (_isCompleted || _queue.Count >= Capacity)
            {
                return false;
            }

            _queue.Enqueue(item);

            // Неблокуюче пробудження одного асинхронного воркера (async_tasks), якщо він очікує
            while (_asyncPulseWaiters.Count > 0)
            {
                var waiter = _asyncPulseWaiters.Dequeue();
                if (waiter.TrySetResult(true))
                {
                    break;
                }
            }

            // Пробудження виділеного системного потоку (dedicated_threads), що очікує в Monitor.Wait
            Monitor.Pulse(_syncRoot);
            return true;
        }
    }

    // Д1.3: Блокуюче очікування елемента за допомогою Monitor.Wait (використовується у dedicated_threads)
    public QueuedRequestItem? DequeueBlocking(CancellationToken cancellationToken)
    {
        using var reg = cancellationToken.Register(() =>
        {
            lock (_syncRoot)
            {
                Monitor.PulseAll(_syncRoot);
            }
        });

        lock (_syncRoot)
        {
            while (_queue.Count == 0)
            {
                if (_isCompleted || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                Monitor.Wait(_syncRoot, millisecondsTimeout: 250);
            }

            return _queue.Dequeue();
        }
    }

    // Д1.1: Справжнє неблокуюче асинхронне очікування без захоплення потоків ThreadPool
    public ValueTask<QueuedRequestItem?> DequeueAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_queue.Count > 0)
            {
                return ValueTask.FromResult<QueuedRequestItem?>(_queue.Dequeue());
            }

            if (_isCompleted || cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult<QueuedRequestItem?>(null);
            }
        }

        return DequeueAsyncSlow(cancellationToken);
    }

    private async ValueTask<QueuedRequestItem?> DequeueAsyncSlow(CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource<bool> waiter;
            lock (_syncRoot)
            {
                if (_queue.Count > 0)
                {
                    return _queue.Dequeue();
                }

                if (_isCompleted || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _asyncPulseWaiters.Enqueue(waiter);
            }

            await using var reg = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false),
                waiter);

            await waiter.Task.ConfigureAwait(false);
        }
    }

    // Д1.4: Переведення черги у стан завершення та пробудження всіх синхронних і асинхронних воркерів
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

// ============================================================================
// Стандартна реалізація обмеженої черги на базі System.Threading.Channels (О1.2)
// Використовується при QUEUE_ENGINE=system_channel
// ============================================================================
public sealed class ChannelRequestQueue : IBoundedRequestQueue
{
    private readonly Channel<QueuedRequestItem> _channel;

    public int Capacity { get; }
    public string EngineName => "system_channel (System.Threading.Channels)";

    public ChannelRequestQueue(int capacity)
    {
        Capacity = capacity;
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<QueuedRequestItem>(options);
    }

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(QueuedRequestItem item)
    {
        return _channel.Writer.TryWrite(item);
    }

    public async ValueTask<QueuedRequestItem?> DequeueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_channel.Reader.TryRead(out var item))
                {
                    return item;
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public QueuedRequestItem? DequeueBlocking(CancellationToken cancellationToken)
    {
        return DequeueAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }

    public void Clear()
    {
        while (_channel.Reader.TryRead(out _)) { }
    }
}
