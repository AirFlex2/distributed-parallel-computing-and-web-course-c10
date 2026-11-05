using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnrollmentService;
using EnrollmentService.Queues;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class DomainAndQueueTests
{
    // РПО Д1.1 та Д1.3: Неблокуюча асинхронна черга MonitorRequestQueue (TaskCompletionSource + обмежена місткість)
    [Fact]
    public async Task MonitorRequestQueue_DequeueAsync_AwaitsNonBlockingAndDrainsOnComplete()
    {
        var queue = new MonitorRequestQueue(capacity: 2);

        // Запуск очікуючого споживача до появи елемента в черзі
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ValueTask<QueuedRequestItem?> pendingDequeue = queue.DequeueAsync(cts.Token);
        Assert.False(pendingDequeue.IsCompleted);

        var item1 = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var item2 = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-2",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var item3Overflow = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-3",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        Assert.True(queue.TryEnqueue(item1));
        var dequeued1 = await pendingDequeue;
        Assert.NotNull(dequeued1);
        Assert.Equal(item1.Id, dequeued1!.Id);

        // Перевірка межі місткості (capacity = 2)
        Assert.True(queue.TryEnqueue(item1));
        Assert.True(queue.TryEnqueue(item2));
        Assert.False(queue.TryEnqueue(item3Overflow));

        // Після Complete() нові елементи відхиляються, але вже прийняті вивантажуються до кінця
        queue.Complete();
        Assert.False(queue.TryEnqueue(item3Overflow));

        var d1 = await queue.DequeueAsync(cts.Token);
        var d2 = await queue.DequeueAsync(cts.Token);
        var dEnd = await queue.DequeueAsync(cts.Token);

        Assert.Equal(item1.Id, d1?.Id);
        Assert.Equal(item2.Id, d2?.Id);
        Assert.Null(dEnd);
    }

    // РПО: Перевірка життєвого циклу резервування слотів у чергах (MonitorRequestQueue та ChannelRequestQueue)
    [Fact]
    public void RequestQueue_SlotReservationLifecycle_TracksAvailableCapacityCorrectly()
    {
        var monitorQueue = new MonitorRequestQueue(capacity: 2);
        Assert.True(monitorQueue.TryReserveSlot());
        Assert.True(monitorQueue.TryReserveSlot());
        Assert.False(monitorQueue.TryReserveSlot()); // capacity exhausted

        // Звільнення слота при помилці/відмові
        monitorQueue.ReleaseSlot();
        Assert.True(monitorQueue.TryReserveSlot());

        var item = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        // Постановка у зарезервований слот
        Assert.True(monitorQueue.EnqueueReserved(item));
        Assert.Equal(1, monitorQueue.Count);

        var channelQueue = new ChannelRequestQueue(capacity: 2);
        Assert.True(channelQueue.TryReserveSlot());
        Assert.True(channelQueue.TryReserveSlot());
        Assert.False(channelQueue.TryReserveSlot());

        channelQueue.ReleaseSlot();
        Assert.True(channelQueue.TryReserveSlot());
        Assert.True(channelQueue.EnqueueReserved(item));
        Assert.Equal(1, channelQueue.Count);
    }

    // РПО Д1.1 та Д1.3: Перевірка ChannelRequestQueue з кількома паралельними воркерами (перевірка while на WaitToReadAsync)
    [Fact]
    public async Task ChannelRequestQueue_MultipleWorkers_ProcessesAllItemsWithoutPrematureExit()
    {
        var queue = new ChannelRequestQueue(capacity: 50);
        int workerCount = 4;
        int totalItems = 30;
        var processed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (true)
            {
                var item = await queue.DequeueAsync(cts.Token);
                if (item == null) break;
                processed.Add(item.Id);
                await Task.Yield();
            }
        }).ToArray();

        for (int i = 0; i < totalItems; i++)
        {
            var item = new QueuedRequestItem
            {
                Id = Guid.NewGuid(),
                StudentId = $"student-{i}",
                GroupId = "DISC-DPC-01",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            Assert.True(queue.TryEnqueue(item));
            if (i % 5 == 0) await Task.Delay(5);
        }

        queue.Complete();
        await Task.WhenAll(workers);

        Assert.Equal(totalItems, processed.Count);
    }
}
