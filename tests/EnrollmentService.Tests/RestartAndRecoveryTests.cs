using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using EnrollmentService.Services.Processing;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed class RestartAndRecoveryTests
{
    private const string ConnectionString = "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password;Maximum Pool Size=20";

    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync(cancellationToken);
            return conn;
        }
    }

    private sealed class DummyHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;
        public void StopApplication() => _stoppingCts.Cancel();
    }

    private readonly TestConnectionFactory _factory = new();
    private readonly EnrollmentRequestRepository _requestRepo;
    private readonly GroupRepository _groupRepo;
    private readonly EnrollmentRepository _enrollmentRepo;
    private readonly DecanateRepository _decanateRepo;

    public RestartAndRecoveryTests()
    {
        var options = new ServiceOptions { InstanceId = "app-test" };
        _requestRepo = new EnrollmentRequestRepository(_factory);
        _groupRepo = new GroupRepository(_factory);
        _enrollmentRepo = new EnrollmentRepository(_factory, options);
        _decanateRepo = new DecanateRepository(_factory);
    }

    private async Task<bool> IsDatabaseAvailableAsync()
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task InsertRequestWithStatusAsync(Guid id, string studentId, string groupId, string status, DateTimeOffset? createdAt = null)
    {
        await using var conn = await _factory.OpenConnectionAsync();

        await using var studentCmd = new NpgsqlCommand(@"
            INSERT INTO student (id, email, full_name, max_disciplines, active_count, active_ects)
            VALUES (@studentId, @email, @fullName, 5, 0, 0.0)
            ON CONFLICT (id) DO NOTHING;", conn);
        studentCmd.Parameters.AddWithValue("studentId", studentId);
        studentCmd.Parameters.AddWithValue("email", $"{studentId}@test.univ.ua");
        studentCmd.Parameters.AddWithValue("fullName", $"Test Student {studentId}");
        await studentCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO enrollment_requests (
                id, student_id, group_id, status, created_at
            ) VALUES (
                @id, @studentId, @groupId, @status, @createdAt
            )
            ON CONFLICT (id) DO UPDATE SET status = @status;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("groupId", groupId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("createdAt", createdAt ?? DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CleanupRequestAsync(Guid id)
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(@"
                DELETE FROM decanate_roster WHERE request_id = @id;
                DELETE FROM enrollment_requests WHERE id = @id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    // 1. Перезапуск застосунку з однією заявкою queued
    [Fact]
    public async Task RestartWithSingleQueuedRequest_RecoversIntoQueueAndReachesTerminalStatus()
    {
        if (!await IsDatabaseAvailableAsync()) return;

        var id = Guid.NewGuid();
        var cleanupList = new List<Guid> { id };

        try
        {
            await InsertRequestWithStatusAsync(id, "student-1", "DISC-DPC-01", "queued");

            var queue = new MonitorRequestQueue(capacity: 10);
            var metrics = new MetricsCollector();
            var options = new ServiceOptions
            {
                InstanceId = "app-test-restart",
                EnableWorkers = true,
                Workers = 1,
                QueueCapacity = 10,
                SlowCallMinMs = 10,
                SlowCallMaxMs = 30
            };
            var processor = new EnrollmentRequestProcessor(_requestRepo, _groupRepo, _enrollmentRepo, _decanateRepo, _factory, metrics, options);
            var lifetime = new DummyHostLifetime();

            var pool = new EnrollmentWorkerPool(queue, processor, _requestRepo, options, metrics, lifetime);

            // Запуск пулу (симуляція старту/перезапуску сервісу)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pool.StartAsync(cts.Token);

            // Очікуємо обробки заявки воркером
            EnrollmentStatusResponseDto? status = null;
            for (int i = 0; i < 50; i++)
            {
                status = await _requestRepo.GetStatusByIdAsync(id);
                if (status != null && status.Status is "done" or "rejected" or "failed")
                {
                    break;
                }
                await Task.Delay(50);
            }

            await pool.StopAsync(CancellationToken.None);

            Assert.NotNull(status);
            Assert.Contains(status.Status, new[] { "done", "rejected", "failed" });
        }
        finally
        {
            foreach (var rId in cleanupList) await CleanupRequestAsync(rId);
        }
    }

    // 2. Перезапуск із кількома заявками queued
    [Fact]
    public async Task RestartWithMultipleQueuedRequests_RecoversAllIntoQueue()
    {
        if (!await IsDatabaseAvailableAsync()) return;

        var ids = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid()).ToList();

        try
        {
            var baseTime = DateTimeOffset.UtcNow;
            for (int i = 0; i < ids.Count; i++)
            {
                await InsertRequestWithStatusAsync(ids[i], $"student-{i + 1}", "DISC-DPC-01", "queued", baseTime.AddSeconds(i));
            }

            var queue = new MonitorRequestQueue(capacity: 10);
            var metrics = new MetricsCollector();
            var options = new ServiceOptions
            {
                InstanceId = "app-test-restart-multi",
                EnableWorkers = true,
                Workers = 2,
                QueueCapacity = 10,
                SlowCallMinMs = 10,
                SlowCallMaxMs = 20
            };
            var processor = new EnrollmentRequestProcessor(_requestRepo, _groupRepo, _enrollmentRepo, _decanateRepo, _factory, metrics, options);
            var lifetime = new DummyHostLifetime();

            var pool = new EnrollmentWorkerPool(queue, processor, _requestRepo, options, metrics, lifetime);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await pool.StartAsync(cts.Token);

            // Чекаємо завершення всіх 4 заявок
            bool allTerminal = false;
            for (int i = 0; i < 60; i++)
            {
                int terminalCount = 0;
                foreach (var id in ids)
                {
                    var s = await _requestRepo.GetStatusByIdAsync(id);
                    if (s != null && s.Status is "done" or "rejected" or "failed") terminalCount++;
                }

                if (terminalCount == ids.Count)
                {
                    allTerminal = true;
                    break;
                }
                await Task.Delay(50);
            }

            await pool.StopAsync(CancellationToken.None);

            Assert.True(allTerminal, "Усі відновлені заявки мають отримати кінцевий статус.");
        }
        finally
        {
            foreach (var rId in ids) await CleanupRequestAsync(rId);
        }
    }

    // 3. Кількість збережених заявок більше QUEUE_CAPACITY (дозавантаження без втрат)
    [Fact]
    public async Task StoredRequestsExceedQueueCapacity_ReplenishesSequentiallyWithoutLoss()
    {
        if (!await IsDatabaseAvailableAsync()) return;

        // 6 заявок при місткості черги 2
        var ids = Enumerable.Range(1, 6).Select(_ => Guid.NewGuid()).ToList();

        try
        {
            var baseTime = DateTimeOffset.UtcNow;
            for (int i = 0; i < ids.Count; i++)
            {
                await InsertRequestWithStatusAsync(ids[i], $"student-{i + 1}", "DISC-DPC-01", "queued", baseTime.AddSeconds(i));
            }

            int queueCapacity = 2;
            var queue = new MonitorRequestQueue(capacity: queueCapacity);
            var metrics = new MetricsCollector();
            var options = new ServiceOptions
            {
                InstanceId = "app-test-overflow-replenish",
                EnableWorkers = true,
                Workers = 2,
                QueueCapacity = queueCapacity,
                SlowCallMinMs = 15,
                SlowCallMaxMs = 25
            };
            var processor = new EnrollmentRequestProcessor(_requestRepo, _groupRepo, _enrollmentRepo, _decanateRepo, _factory, metrics, options);
            var lifetime = new DummyHostLifetime();

            var pool = new EnrollmentWorkerPool(queue, processor, _requestRepo, options, metrics, lifetime);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pool.StartAsync(cts.Token);

            // Очікуємо, поки всі 6 заявок будуть послідовно підтягнуті з БД та завершені
            bool allDoneOrTerminal = false;
            for (int i = 0; i < 80; i++)
            {
                int finished = 0;
                foreach (var id in ids)
                {
                    var s = await _requestRepo.GetStatusByIdAsync(id);
                    if (s != null && s.Status is "done" or "rejected" or "failed") finished++;
                }

                if (finished == ids.Count)
                {
                    allDoneOrTerminal = true;
                    break;
                }
                await Task.Delay(50);
            }

            await pool.StopAsync(CancellationToken.None);

            Assert.True(allDoneOrTerminal, "Усі 6 заявок (при місткості черги 2) мають бути дозавантажені та завершені.");
        }
        finally
        {
            foreach (var rId in ids) await CleanupRequestAsync(rId);
        }
    }

    // 4. Перезапуск із заявками processing
    [Fact]
    public async Task RestartWithProcessingRequests_RecoversStuckRequestsToTerminalStatus()
    {
        if (!await IsDatabaseAvailableAsync()) return;

        var id = Guid.NewGuid();

        try
        {
            await InsertRequestWithStatusAsync(id, "student-1", "DISC-DPC-01", "processing");

            // Викликаємо відновлення завислих processing-заявок
            int recovered = await _requestRepo.RecoverStuckProcessingRequestsAsync();

            Assert.True(recovered >= 1);

            var status = await _requestRepo.GetStatusByIdAsync(id);
            Assert.NotNull(status);
            // Заявка не була у відомості, тому переведена у 'failed' з поясненням
            Assert.Equal("failed", status.Status);
            Assert.NotNull(status.RejectionReason);
            Assert.Contains("перезапуском", status.RejectionReason);
        }
        finally
        {
            await CleanupRequestAsync(id);
        }
    }

    // 5. Перевірка відсутності подвійної обробки однієї заявки
    [Fact]
    public async Task NoDuplicateProcessing_WhenTwoWorkersAttemptSameItem_OnlyOneClaims()
    {
        if (!await IsDatabaseAvailableAsync()) return;

        var id = Guid.NewGuid();

        try
        {
            await InsertRequestWithStatusAsync(id, "student-1", "DISC-DPC-01", "queued");

            var now = DateTimeOffset.UtcNow;
            // Два воркери одночасно намагаються взяти заявку через атомарний TryMarkStartedAsync
            var task1 = _requestRepo.TryMarkStartedAsync(id, now);
            var task2 = _requestRepo.TryMarkStartedAsync(id, now);

            var results = await Task.WhenAll(task1, task2);

            // Рівно один воркер повинен отримати true, інший - false
            int successCount = results.Count(r => r);
            int failureCount = results.Count(r => !r);

            Assert.Equal(1, successCount);
            Assert.Equal(1, failureCount);

            // Повторна спроба від будь-якого воркера також повертає false
            bool thirdAttempt = await _requestRepo.TryMarkStartedAsync(id, now);
            Assert.False(thirdAttempt);
        }
        finally
        {
            await CleanupRequestAsync(id);
        }
    }

    // 6. Перевірка, що після відновлення всі заявки отримують кінцевий статус
    [Fact]
    public async Task AllRecoveredRequests_EventuallyReceiveTerminalStatus()
    {
        if (!await IsDatabaseAvailableAsync()) return;

        var qId1 = Guid.NewGuid();
        var qId2 = Guid.NewGuid();
        var pId = Guid.NewGuid();
        var allIds = new List<Guid> { qId1, qId2, pId };

        try
        {
            await InsertRequestWithStatusAsync(qId1, "student-1", "DISC-DPC-01", "queued");
            await InsertRequestWithStatusAsync(qId2, "student-2", "DISC-DPC-01", "queued");
            await InsertRequestWithStatusAsync(pId, "student-3", "DISC-DPC-01", "processing");

            var queue = new MonitorRequestQueue(capacity: 10);
            var metrics = new MetricsCollector();
            var options = new ServiceOptions
            {
                InstanceId = "app-test-all-terminal",
                EnableWorkers = true,
                Workers = 2,
                QueueCapacity = 10,
                SlowCallMinMs = 10,
                SlowCallMaxMs = 20
            };
            var processor = new EnrollmentRequestProcessor(_requestRepo, _groupRepo, _enrollmentRepo, _decanateRepo, _factory, metrics, options);
            var lifetime = new DummyHostLifetime();

            var pool = new EnrollmentWorkerPool(queue, processor, _requestRepo, options, metrics, lifetime);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await pool.StartAsync(cts.Token);

            for (int i = 0; i < 50; i++)
            {
                var s1 = await _requestRepo.GetStatusByIdAsync(qId1);
                var s2 = await _requestRepo.GetStatusByIdAsync(qId2);
                var sp = await _requestRepo.GetStatusByIdAsync(pId);

                if (s1?.Status is "done" or "rejected" or "failed" &&
                    s2?.Status is "done" or "rejected" or "failed" &&
                    sp?.Status is "done" or "rejected" or "failed")
                {
                    break;
                }
                await Task.Delay(50);
            }

            await pool.StopAsync(CancellationToken.None);

            foreach (var id in allIds)
            {
                var finalState = await _requestRepo.GetStatusByIdAsync(id);
                Assert.NotNull(finalState);
                Assert.Contains(finalState.Status, new[] { "done", "rejected", "failed" });
            }
        }
        finally
        {
            foreach (var rId in allIds) await CleanupRequestAsync(rId);
        }
    }
}
