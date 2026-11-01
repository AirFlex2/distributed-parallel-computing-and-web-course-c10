using System.Threading.Channels;

namespace EnrollmentService.Queues;

// ============================================================================
// Стандартна реалізація обмеженої черги на базі System.Threading.Channels (О1.2)
// Використовується при QUEUE_ENGINE=system_channel
// ============================================================================
public sealed class ChannelRequestQueue : IBoundedRequestQueue
{
    private readonly Channel<QueuedRequestItem> _channel;
    private readonly object _syncRoot = new();
    private int _reservedSlots;

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

    public bool TryReserveSlot()
    {
        lock (_syncRoot)
        {
            if ((_channel.Reader.Count + _reservedSlots) >= Capacity)
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
            if (_channel.Writer.TryWrite(item))
            {
                if (_reservedSlots > 0)
                {
                    _reservedSlots--;
                }
                return true;
            }
            return false;
        }
    }

    public bool TryEnqueue(QueuedRequestItem item)
    {
        lock (_syncRoot)
        {
            if ((_channel.Reader.Count + _reservedSlots) >= Capacity)
            {
                return false;
            }
            return _channel.Writer.TryWrite(item);
        }
    }

    public async ValueTask<QueuedRequestItem?> DequeueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_channel.Reader.TryRead(out var item)) return item;
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
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_channel.Reader.TryRead(out var item))
                {
                    return item;
                }

                bool hasData = _channel.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
                if (!hasData)
                {
                    return null;
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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
