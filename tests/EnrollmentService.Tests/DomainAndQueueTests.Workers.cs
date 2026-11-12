using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnrollmentService;
using EnrollmentService.Http;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Services;
using EnrollmentService.Services.Processing;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class DomainAndQueueTests
{
    // Д1.4: Перевірка обов'язкового Bearer-токена для /metrics та /v1/groups/{id}/close
    [Fact]
    public async Task BearerAuthMiddleware_EnforcesTokenOnMetricsAndGroupClose()
    {
        var opts = new ServiceOptions { ApiAuthToken = "test-token-123" };
        var nextCalled = false;
        RequestDelegate next = (ctx) => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new BearerAuthMiddleware(next, opts);

        // 1. /metrics без токена -> 401 Unauthorized
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Path = "/metrics";
        ctx1.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(ctx1);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx1.Response.StatusCode);

        // 2. /metrics з недійсним токеном -> 401 Unauthorized
        nextCalled = false;
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Path = "/metrics";
        ctx2.Request.Headers.Authorization = "Bearer wrong-token";
        ctx2.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(ctx2);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx2.Response.StatusCode);

        // 3. /metrics з валідним токеном -> доступ дозволено (виклик наступного middleware)
        nextCalled = false;
        var ctx3 = new DefaultHttpContext();
        ctx3.Request.Path = "/metrics";
        ctx3.Request.Headers.Authorization = "Bearer test-token-123";
        await middleware.InvokeAsync(ctx3);
        Assert.True(nextCalled);

        // 4. /v1/groups/DISC-01/close без токена -> 401 Unauthorized
        nextCalled = false;
        var ctx4 = new DefaultHttpContext();
        ctx4.Request.Path = "/v1/groups/DISC-01/close";
        ctx4.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(ctx4);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx4.Response.StatusCode);

        // 5. /health без токена -> доступ дозволено без автентифікації
        nextCalled = false;
        var ctx5 = new DefaultHttpContext();
        ctx5.Request.Path = "/health";
        await middleware.InvokeAsync(ctx5);
        Assert.True(nextCalled);
    }

    // РПО Д1.4: Тестування сценарію Graceful Shutdown (SIGTERM) при повністю заповненій черзі:
    // 1) нові запити відхиляються з кодом 503 Service Unavailable (service_shutting_down),
    // 2) активний пул робочих потоків (EnrollmentWorkerPool) безперешкодно дообробляє всі раніше прийняті заявки
    //    до кінцевого стану (done),
    // 3) черга повністю спустошується, воркери безпечно зупиняються без витоків, помилок або втрати даних.
    [Fact]
    public async Task Dpc_D14_GracefulShutdown_FullQueue_RejectsNewRequestsWith503_AndDrainsAllQueuedRequests()
    {
        const int capacity = 3;
        var queue = new MonitorRequestQueue(capacity);
        var options = new ServiceOptions
        {
            InstanceId = "test-graceful-node",
            ExecutionModel = "async_tasks",
            QueueCapacity = capacity,
            EnableWorkers = true,
            Workers = 2
        };
        var metrics = new MetricsCollector();
        var lifetime = new TestHostLifetime();
        var testProcessor = new TestShutdownProcessor();
        var pool = new EnrollmentWorkerPool(queue, testProcessor, null, options, metrics, lifetime);

        // 1. Заповнюємо чергу до максимальної місткості
        var queuedItems = new List<QueuedRequestItem>();
        for (int i = 1; i <= capacity; i++)
        {
            var item = new QueuedRequestItem
            {
                Id = Guid.NewGuid(),
                StudentId = $"student-{i}",
                GroupId = "DISC-DPC-01",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            queuedItems.Add(item);
            bool enqueued = queue.TryEnqueue(item);
            Assert.True(enqueued, $"Item {i} should be successfully enqueued.");
        }
        Assert.Equal(capacity, queue.Count);

        // 2. Запускаємо реальний пул воркерів
        await pool.StartAsync(CancellationToken.None);

        // 3. Ініціюємо Graceful Shutdown (імітація сигналу SIGTERM Д1.4)
        pool.InitiateShutdown();
        Assert.True(pool.IsShuttingDown);

        // 4. Перевіряємо роботу GracefulShutdownMiddleware:
        // Змінюючий POST запит повинен отримати HTTP 503 "service_shutting_down"
        bool nextCalled = false;
        var middleware = new GracefulShutdownMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            options,
            pool);

        var postContext = new DefaultHttpContext();
        postContext.Request.Method = HttpMethods.Post;
        postContext.Request.Path = "/v1/enrollment-requests";
        postContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(postContext);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, postContext.Response.StatusCode);

        // Перевіряємо, що черга більше не приймає нових елементів після завершення
        var overflowItem = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "overflow-student",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        Assert.False(queue.TryEnqueue(overflowItem));

        // 5. Перевіряємо, що читаючий GET запит (перевірка стану) не блокується middleware
        nextCalled = false;
        var getContext = new DefaultHttpContext();
        getContext.Request.Method = HttpMethods.Get;
        getContext.Request.Path = "/v1/enrollment-requests/" + queuedItems[0].Id;
        await middleware.InvokeAsync(getContext);
        Assert.True(nextCalled);

        // 6. Чекаємо коректного завершення роботи воркерів (StopAsync виконує повне вивантаження черги)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pool.StopAsync(cts.Token);

        // 7. Перевіряємо, що всі заявки були реально оброблені пулом воркерів і переведені у статус done
        Assert.Equal(capacity, testProcessor.ProcessedStatus.Count);
        foreach (var item in queuedItems)
        {
            Assert.True(testProcessor.ProcessedStatus.TryGetValue(item.Id, out var status));
            Assert.Equal("done", status);
        }

        // 8. Черга повністю порожня, застосунок сигналізував про завершення
        Assert.Equal(0, queue.Count);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task WebEnrollmentNotifier_FinalAttemptException_ReturnsFalseWithoutThrowing()
    {
        var mockService = new ThrowingConfirmationService(new InvalidOperationException("Fatal DB error on attempt"));
        var notifier = new WebEnrollmentNotifier(mockService);

        bool result = await notifier.NotifyRosterRecordedAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(3, mockService.CallCount);
    }

    [Fact]
    public async Task WebEnrollmentNotifier_CancellationRequested_RethrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockService = new ThrowingConfirmationService(new OperationCanceledException(cts.Token));
        var notifier = new WebEnrollmentNotifier(mockService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await notifier.NotifyRosterRecordedAsync(Guid.NewGuid(), Guid.NewGuid(), cts.Token);
        });
    }

    [Fact]
    public async Task WebEnrollmentNotifier_PermanentFailure_ReturnsFalseImmediatelyWithoutRetrying()
    {
        var stubService = new StubConfirmationService(RosterConfirmationResult.PermanentFailure);
        var notifier = new WebEnrollmentNotifier(stubService);

        bool result = await notifier.NotifyRosterRecordedAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, stubService.CallCount);
    }

    [Fact]
    public async Task EnrollmentWorkerPool_WhenEnableWorkersFalse_ShutdownDoesNotDrainAndSignalsStopImmediately()
    {
        var queue = new MonitorRequestQueue(capacity: 10);
        var metrics = new MetricsCollector();
        var options = new ServiceOptions
        {
            InstanceId = "app-b",
            EnableWorkers = false,
            Workers = 3,
            QueueCapacity = 10
        };
        var lifetime = new TestHostLifetime();
        var pool = new EnrollmentWorkerPool(queue, null, null, options, metrics, lifetime);

        await pool.StartAsync(CancellationToken.None);

        pool.InitiateShutdown();
        Assert.True(pool.IsShuttingDown);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await pool.StopAsync(stopCts.Token);
        Assert.Equal(0, queue.Count);
    }

    private sealed class ThrowingConfirmationService : IWebEnrollmentConfirmationService
    {
        private readonly Exception _exceptionToThrow;
        public int CallCount { get; private set; }

        public ThrowingConfirmationService(Exception exceptionToThrow)
        {
            _exceptionToThrow = exceptionToThrow;
        }

        public Task<RosterConfirmationResult> ConfirmEnrollmentDetailedAsync(
            Guid enrollmentId,
            Guid requestId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw _exceptionToThrow;
        }
    }

    private sealed class StubConfirmationService : IWebEnrollmentConfirmationService
    {
        private readonly RosterConfirmationResult _result;
        public int CallCount { get; private set; }

        public StubConfirmationService(RosterConfirmationResult result)
        {
            _result = result;
        }

        public Task<RosterConfirmationResult> ConfirmEnrollmentDetailedAsync(
            Guid enrollmentId,
            Guid requestId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class TestShutdownProcessor : IEnrollmentRequestProcessor
    {
        public ConcurrentDictionary<Guid, string> ProcessedStatus { get; } = new();

        public async Task ProcessItemAsync(
            int workerId,
            QueuedRequestItem item,
            bool isAsyncModel,
            IBoundedRequestQueue queue,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            ProcessedStatus[item.Id] = "done";
        }
    }

    private sealed class TestHostLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;
        public void StopApplication() => _stoppingCts.Cancel();
    }
}
