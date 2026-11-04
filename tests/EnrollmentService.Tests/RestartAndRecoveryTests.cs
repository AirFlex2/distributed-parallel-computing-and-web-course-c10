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
    private sealed class DummyHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;
        public void StopApplication() => _stoppingCts.Cancel();
    }

    private readonly IDbConnectionFactory _factory = TestEnvironment.CreateDbConnectionFactory();
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

    private static Task<bool> IsDatabaseAvailableAsync() => TestEnvironment.IsDatabaseAvailableAsync();

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

    private async Task InsertRosterEntryAsync(Guid requestId, string studentId, string groupId, string disciplineId, decimal ects)
    {
        await using var conn = await _factory.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO decanate_roster (
                request_id, student_id, group_id, discipline_id, ects, assignment_type, created_at
            ) VALUES (
                @requestId, @studentId, @groupId, @disciplineId, @ects, 'primary', NOW()
            )
            ON CONFLICT (request_id) DO NOTHING;", conn);
        cmd.Parameters.AddWithValue("requestId", requestId);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("groupId", groupId);
        cmd.Parameters.AddWithValue("disciplineId", disciplineId);
        cmd.Parameters.AddWithValue("ects", ects);
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RestartAndRecoveryTests Error]: Помилка очищення заявки '{id}': {ex}");
            throw;
        }
    }

    // 1. Перезапуск застосунку з однією заявкою queued
    [SkippableFact]
    public async Task RestartWithSingleQueuedRequest_RecoversIntoQueueAndReachesTerminalStatus()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pool.StartAsync(cts.Token);

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
    [SkippableFact]
    public async Task RestartWithMultipleQueuedRequests_RecoversAllIntoQueue()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

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
    [SkippableFact]
    public async Task StoredRequestsExceedQueueCapacity_ReplenishesSequentiallyWithoutLoss()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

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

    // 4.1 Відновлення processing: аварія ДО транзакції (немає запису у відомості -> перехід у failed без списання місць)
    [SkippableFact]
    public async Task RecoverProcessing_CrashBeforeTransaction_SetsFailedAndLeavesSeatsUntouched()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();

        try
        {
            // Симуляція падіння сервера під час повільного виклику або до відкриття транзакції
            await InsertRequestWithStatusAsync(id, "student-1", "DISC-DPC-01", "processing");

            // Виклик відновлення завислих processing-заявок
            int recovered = await _requestRepo.RecoverStuckProcessingRequestsAsync();
            Assert.True(recovered >= 1);

            var status = await _requestRepo.GetStatusByIdAsync(id);
            Assert.NotNull(status);
            Assert.Equal("failed", status.Status);
            Assert.NotNull(status.RejectionReason);
            Assert.Contains("перезапуском", status.RejectionReason);
            Assert.False(status.RecordedInRoster);
            Assert.NotNull(status.FinishedAt);
        }
        finally
        {
            await CleanupRequestAsync(id);
        }
    }

    // 4.2 Відновлення processing: аварія ПІСЛЯ фіксації відомості деканату (є запис у відомості -> перехід у done)
    [SkippableFact]
    public async Task RecoverProcessing_CrashAfterRosterCommit_SetsDoneAndPreservesRosterEntry()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();

        try
        {
            // Симуляція ситуації: транзакція зафіксувала запис у decanate_roster, але сервер упав до оновлення enrollment_requests
            await InsertRequestWithStatusAsync(id, "student-2", "DISC-DPC-01", "processing");
            await InsertRosterEntryAsync(id, "student-2", "DISC-DPC-01", "DISC-DPC", 6.0m);

            // Виклик відновлення
            int recovered = await _requestRepo.RecoverStuckProcessingRequestsAsync();
            Assert.True(recovered >= 1);

            var status = await _requestRepo.GetStatusByIdAsync(id);
            Assert.NotNull(status);
            // Оскільки запис у decanate_roster існує, статус переведено у 'done' без помилки відхилення
            Assert.Equal("done", status.Status);
            Assert.Null(status.RejectionReason);
            Assert.True(status.RecordedInRoster);
            Assert.NotNull(status.FinishedAt);
        }
        finally
        {
            await CleanupRequestAsync(id);
        }
    }

    // 4.3 Відновлення processing: одночасний старт кількох воркерів під час відновлення (захист від гонитви)
    [SkippableFact]
    public async Task ConcurrentWorkerStartup_DuringRecovery_GuaranteesNoDuplicateProcessing()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var ids = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToList();

        try
        {
            for (int i = 0; i < ids.Count; i++)
            {
                await InsertRequestWithStatusAsync(ids[i], $"student-{i + 1}", "DISC-DPC-01", "queued");
            }

            var now = DateTimeOffset.UtcNow;
            // 4 паралельні воркери одночасно змагаються за кожну заявку через атомарний TryMarkStartedAsync
            var claimResults = new System.Collections.Concurrent.ConcurrentBag<(Guid Id, int WorkerId)>();

            var workerTasks = Enumerable.Range(1, 4).Select(workerId => Task.Run(async () =>
            {
                foreach (var id in ids)
                {
                    bool claimed = await _requestRepo.TryMarkStartedAsync(id, now);
                    if (claimed)
                    {
                        claimResults.Add((id, workerId));
                    }
                }
            })).ToArray();

            await Task.WhenAll(workerTasks);

            // Кожна заявка повинна бути захоплена РІВНО ОДНИМ воркером
            var grouped = claimResults.GroupBy(c => c.Id).ToList();
            Assert.Equal(ids.Count, grouped.Count);
            foreach (var g in grouped)
            {
                Assert.Single(g); // Рівно 1 переможець на кожну заявку
            }
        }
        finally
        {
            foreach (var rId in ids) await CleanupRequestAsync(rId);
        }
    }

    // 5. Перевірка відсутності подвійної обробки однієї заявки двома воркерами
    [SkippableFact]
    public async Task NoDuplicateProcessing_WhenTwoWorkersAttemptSameItem_OnlyOneClaims()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();

        try
        {
            await InsertRequestWithStatusAsync(id, "student-1", "DISC-DPC-01", "queued");

            var now = DateTimeOffset.UtcNow;
            var task1 = _requestRepo.TryMarkStartedAsync(id, now);
            var task2 = _requestRepo.TryMarkStartedAsync(id, now);

            var results = await Task.WhenAll(task1, task2);

            int successCount = results.Count(r => r);
            int failureCount = results.Count(r => !r);

            Assert.Equal(1, successCount);
            Assert.Equal(1, failureCount);

            bool thirdAttempt = await _requestRepo.TryMarkStartedAsync(id, now);
            Assert.False(thirdAttempt);
        }
        finally
        {
            await CleanupRequestAsync(id);
        }
    }

    // 6. Перевірка, що після відновлення всі заявки отримують кінцевий статус
    [SkippableFact]
    public async Task AllRecoveredRequests_EventuallyReceiveTerminalStatus()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

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

    [SkippableFact]
    public async Task JointScenario_AttemptInUnsafeMode_Rejected()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-unsafe-joint";
        string groupId = "GRP-UNSAFE-JOINT";
        string discId = "DISC-UNSAFE-JOINT";
        Guid fakeEnrollmentId = Guid.NewGuid();
        Guid testRequestId = Guid.NewGuid();

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, testRequestId);
        try
        {
            await using var conn = await _factory.OpenConnectionAsync();

            await using var cmdDisc = new NpgsqlCommand(@"
                INSERT INTO discipline (id, code, title, ects, is_test_only)
                VALUES (@discId, 'CODE-' || @discId, 'Test Discipline ' || @discId, 3.0, TRUE)
                ON CONFLICT (id) DO UPDATE SET ects = 3.0;", conn);
            cmdDisc.Parameters.AddWithValue("discId", discId);
            await cmdDisc.ExecuteNonQueryAsync();

            await using var cmdGroup = new NpgsqlCommand(@"
                INSERT INTO discipline_group (id, discipline_id, group_code, seats, available, opens_at, closes_at, university_tz, closed, dpc_sandbox_enabled)
                VALUES (@groupId, @discId, 'GRP-' || @groupId, 5, 5, NOW() - INTERVAL '1 day', NOW() + INTERVAL '1 day', 'Europe/Kyiv', FALSE, TRUE)
                ON CONFLICT (id) DO UPDATE SET seats = 5, available = 5, closed = FALSE, dpc_sandbox_enabled = TRUE;", conn);
            cmdGroup.Parameters.AddWithValue("groupId", groupId);
            cmdGroup.Parameters.AddWithValue("discId", discId);
            await cmdGroup.ExecuteNonQueryAsync();

            await using var studentCmd = new NpgsqlCommand(@"
                INSERT INTO student (id, email, full_name, max_disciplines, active_count, active_ects)
                VALUES (@studentId, @email, @fullName, 5, 0, 0.0)
                ON CONFLICT (id) DO NOTHING;", conn);
            studentCmd.Parameters.AddWithValue("studentId", studentId);
            studentCmd.Parameters.AddWithValue("email", $"{studentId}@test.univ.ua");
            studentCmd.Parameters.AddWithValue("fullName", $"Test Student {studentId}");
            await studentCmd.ExecuteNonQueryAsync();

            await using var insReq = new NpgsqlCommand(@"
                INSERT INTO enrollment_requests (id, student_id, group_id, status, created_at, requested_enrollment_id)
                VALUES (@reqId, @sid, @gid, 'queued', NOW(), @reqEnrId);", conn);
            insReq.Parameters.AddWithValue("reqId", testRequestId);
            insReq.Parameters.AddWithValue("sid", studentId);
            insReq.Parameters.AddWithValue("gid", groupId);
            insReq.Parameters.AddWithValue("reqEnrId", fakeEnrollmentId);
            await insReq.ExecuteNonQueryAsync();

            var metrics = new MetricsCollector();
            var queue = new ChannelRequestQueue(10);
            var unsafeOptions = new ServiceOptions
            {
                InstanceId = "test-unsafe",
                UnsafeMode = true,
                AllowTestOverrides = true,
                SlowCallMinMs = 0,
                SlowCallMaxMs = 0
            };

            var unsafeProcessor = new EnrollmentRequestProcessor(
                _requestRepo,
                _groupRepo,
                _enrollmentRepo,
                _decanateRepo,
                _factory,
                metrics,
                unsafeOptions);

            var item = new QueuedRequestItem
            {
                Id = testRequestId,
                StudentId = studentId,
                GroupId = groupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                EnrollmentId = null,
                RequestedEnrollmentId = fakeEnrollmentId
            };

            await unsafeProcessor.ProcessItemAsync(1, item, isAsyncModel: true, queue, CancellationToken.None);

            await using var check = new NpgsqlCommand("SELECT status, rejection_reason FROM enrollment_requests WHERE id = @id;", conn);
            check.Parameters.AddWithValue("id", testRequestId);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("rejected", reader.GetString(0));
            Assert.Contains("Спільний наскрізний сценарій Web + DPC підтримується виключно в безпечному режимі SAFE", reader.GetString(1));
            await reader.CloseAsync();

            await using var checkRoster = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE request_id = @id;", conn);
            checkRoster.Parameters.AddWithValue("id", testRequestId);
            long count = (long)(await checkRoster.ExecuteScalarAsync())!;
            Assert.Equal(0L, count);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, testRequestId);
        }
    }
}
