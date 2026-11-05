using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using EnrollmentService.Services.Processing;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class RestartAndRecoveryTests
{
    // 3.1 Graceful Shutdown на SIGTERM, коли кількість збережених у БД заявок перевищує місткість черги в пам'яті
    [SkippableFact]
    public async Task GracefulShutdown_WhenQueuedRequestsExceedQueueCapacity_DrainsAllFromDbAndCompletesGracefully()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        // Створюємо 12 заявок у БД, тоді як ємність оперативної черги дорівнює лише 3 (12 > 3)
        var ids = Enumerable.Range(1, 12).Select(_ => Guid.NewGuid()).ToList();

        try
        {
            var baseTime = DateTimeOffset.UtcNow;
            for (int i = 0; i < ids.Count; i++)
            {
                await InsertRequestWithStatusAsync(ids[i], $"student-drain-{i + 1}", "DISC-DPC-01", "queued", baseTime.AddSeconds(i));
            }

            int queueCapacity = 3;
            var queue = new MonitorRequestQueue(capacity: queueCapacity);
            var metrics = new MetricsCollector();
            var options = new ServiceOptions
            {
                InstanceId = "app-test-drain-shutdown",
                EnableWorkers = true,
                Workers = 2,
                QueueCapacity = queueCapacity,
                SlowCallMinMs = 10,
                SlowCallMaxMs = 20
            };
            var processor = new EnrollmentRequestProcessor(_requestRepo, _groupRepo, _enrollmentRepo, _decanateRepo, _factory, metrics, options);
            var lifetime = new DummyHostLifetime();

            var pool = new EnrollmentWorkerPool(queue, processor, _requestRepo, options, metrics, lifetime);

            // Запускаємо пул воркерів
            await pool.StartAsync(CancellationToken.None);

            // Надсилаємо сигнал завершення (SIGTERM Graceful Shutdown)
            pool.InitiateShutdown();
            Assert.True(pool.IsShuttingDown);

            // Очікуємо повного завершення DrainAndStopAsync через StopAsync
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pool.StopAsync(stopCts.Token);

            // ПЕРЕВІРКА ІНВАРІАНТА: абсолютно всі 12 заявок (при черзі всього 3)
            // були вивантажені з БД і дійшли до термінального стану
            var terminalStatuses = new List<string>();
            foreach (var id in ids)
            {
                var s = await _requestRepo.GetStatusByIdAsync(id);
                Assert.NotNull(s);
                Assert.Contains(s.Status, new[] { "done", "rejected" });
                terminalStatuses.Add(s.Status);
            }

            Assert.Equal(12, terminalStatuses.Count);
        }
        finally
        {
            foreach (var rId in ids) await CleanupRequestAsync(rId);
        }
    }

    [SkippableFact]
    public async Task GracefulShutdown_AppB_WhenEnableWorkersFalse_DoesNotDrainOrStealQueuedRequests_ShutsDownImmediately()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        // Створюємо 5 заявок у БД зі статусом queued
        var ids = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToList();

        try
        {
            var baseTime = DateTimeOffset.UtcNow;
            for (int i = 0; i < ids.Count; i++)
            {
                await InsertRequestWithStatusAsync(ids[i], $"student-appb-drain-{i + 1}", "DISC-DPC-01", "queued", baseTime.AddSeconds(i));
            }

            int queueCapacity = 10;
            var queue = new MonitorRequestQueue(capacity: queueCapacity);
            var metrics = new MetricsCollector();
            var options = new ServiceOptions
            {
                InstanceId = "app-b",
                EnableWorkers = false, // Вузол app-b без воркерів
                Workers = 3,
                QueueCapacity = queueCapacity,
                SlowCallMinMs = 10,
                SlowCallMaxMs = 20
            };
            var processor = new EnrollmentRequestProcessor(_requestRepo, _groupRepo, _enrollmentRepo, _decanateRepo, _factory, metrics, options);
            var lifetime = new DummyHostLifetime();

            var pool = new EnrollmentWorkerPool(queue, processor, _requestRepo, options, metrics, lifetime);

            // 1. Запуск пулу на app-b: не повинен дозавантажувати заявки з БД
            await pool.StartAsync(CancellationToken.None);
            Assert.Equal(0, queue.Count);

            // 2. Ініціюємо зупинку SIGTERM на вузлі app-b при наявності queued-заявок у БД
            pool.InitiateShutdown();
            Assert.True(pool.IsShuttingDown);

            // 3. Зупинка повинна виконатися миттєво без блокування або зависання в очікуванні відсутніх воркерів
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pool.StopAsync(stopCts.Token);

            // 4. Перевірка: StopApplication викликано, черга не захоплювала заявки
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Equal(0, queue.Count);

            // 5. Перевірка цілісності: усі 5 заявок у БД залишилися недоторканими у статусі 'queued' для робочого вузла app-a
            foreach (var id in ids)
            {
                var s = await _requestRepo.GetStatusByIdAsync(id);
                Assert.NotNull(s);
                Assert.Equal("queued", s.Status);
            }
        }
        finally
        {
            foreach (var rId in ids) await CleanupRequestAsync(rId);
        }
    }
}
