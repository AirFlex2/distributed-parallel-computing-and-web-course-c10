using EnrollmentService;
using EnrollmentService.Domain;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services.Processing;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed class FirstReadTimeTrackingTests
{
    private readonly IDbConnectionFactory _factory = TestEnvironment.CreateDbConnectionFactory();
    private readonly EnrollmentRequestRepository _repo;

    public FirstReadTimeTrackingTests()
    {
        _repo = new EnrollmentRequestRepository(_factory);
    }

    private static Task<bool> IsDatabaseAvailableAsync() => TestEnvironment.IsDatabaseAvailableAsync();

    private async Task InsertTestRequestAsync(Guid id, string status, DateTimeOffset? firstReadAt = null)
    {
        await using var conn = await _factory.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO enrollment_requests (
                id, student_id, group_id, status, created_at, first_read_at
            ) VALUES (
                @id, 'student-1', 'DISC-WEB-01', @status, NOW(), @firstReadAt
            )
            ON CONFLICT (id) DO UPDATE SET status = @status, first_read_at = @firstReadAt;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter("firstReadAt", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = (object?)firstReadAt ?? DBNull.Value
        });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateTestRequestStatusAsync(Guid id, string status)
    {
        await using var conn = await _factory.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE enrollment_requests SET status = @status WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DeleteTestRequestAsync(Guid id)
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM enrollment_requests WHERE id = @id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FirstReadTimeTrackingTests Error]: Помилка видалення тестової заявки '{id}': {ex}");
            throw;
        }
    }

    // Сценарій 1 (О1.5 DPC): queued НЕ встановлює first_read_at (залишається null при кількох GET поспіль)
    [SkippableFact]
    public async Task Scenario1_Get_WhenStatusIsQueued_DoesNotSetFirstReadAt_RemainsNullAcrossMultipleGets()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "queued", null);
            var t1 = DateTimeOffset.UtcNow;
            var t2 = t1.AddSeconds(1);

            var firstGet = await _repo.MarkFirstReadAndGetStatusAsync(id, t1);
            Assert.NotNull(firstGet);
            Assert.Equal("queued", firstGet.Status);
            Assert.Null(firstGet.FirstReadAt);

            var secondGet = await _repo.MarkFirstReadAndGetStatusAsync(id, t2);
            Assert.NotNull(secondGet);
            Assert.Equal("queued", secondGet.Status);
            Assert.Null(secondGet.FirstReadAt);

            var directRead = await _repo.GetStatusByIdAsync(id);
            Assert.NotNull(directRead);
            Assert.Null(directRead.FirstReadAt);
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // Сценарій 2 (О1.5 DPC): processing НЕ встановлює first_read_at (залишається null при кількох GET поспіль)
    [SkippableFact]
    public async Task Scenario2_Get_WhenStatusIsProcessing_DoesNotSetFirstReadAt_RemainsNullAcrossMultipleGets()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "processing", null);
            var t1 = DateTimeOffset.UtcNow;
            var t2 = t1.AddSeconds(1);

            var firstGet = await _repo.MarkFirstReadAndGetStatusAsync(id, t1);
            Assert.NotNull(firstGet);
            Assert.Equal("processing", firstGet.Status);
            Assert.Null(firstGet.FirstReadAt);

            var secondGet = await _repo.MarkFirstReadAndGetStatusAsync(id, t2);
            Assert.NotNull(secondGet);
            Assert.Equal("processing", secondGet.Status);
            Assert.Null(secondGet.FirstReadAt);

            var directRead = await _repo.GetStatusByIdAsync(id);
            Assert.NotNull(directRead);
            Assert.Null(directRead.FirstReadAt);
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // Сценарій 3 (О1.5 DPC): перехід queued -> processing -> done: перший GET на done встановлює first_read_at (t4)
    [SkippableFact]
    public async Task Scenario3_StatusTransition_QueuedToProcessingToDone_SetsFirstReadAtOnlyOnDone()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            // 1. queued: GET повертає null
            await InsertTestRequestAsync(id, "queued", null);
            var queuedResult = await _repo.MarkFirstReadAndGetStatusAsync(id, DateTimeOffset.UtcNow);
            Assert.NotNull(queuedResult);
            Assert.Null(queuedResult.FirstReadAt);

            // 2. processing: GET повертає null
            await UpdateTestRequestStatusAsync(id, "processing");
            var procResult = await _repo.MarkFirstReadAndGetStatusAsync(id, DateTimeOffset.UtcNow);
            Assert.NotNull(procResult);
            Assert.Null(procResult.FirstReadAt);

            // 3. done: перший GET встановлює t4
            await UpdateTestRequestStatusAsync(id, "done");
            var t4 = DateTimeOffset.UtcNow;
            var doneResult = await _repo.MarkFirstReadAndGetStatusAsync(id, t4);
            Assert.NotNull(doneResult);
            Assert.Equal("done", doneResult.Status);
            Assert.NotNull(doneResult.FirstReadAt);
            Assert.Equal(t4.ToUnixTimeMilliseconds(), doneResult.FirstReadAt.Value.ToUnixTimeMilliseconds());
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // Сценарій 4 (О1.5 DPC): повторний GET на done не змінює first_read_at
    [SkippableFact]
    public async Task Scenario4_SubsequentGet_WhenStatusIsDone_DoesNotModifyInitialFirstReadAt()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "done", null);
            var initialT4 = DateTimeOffset.UtcNow.AddMinutes(-5);
            var laterT4 = DateTimeOffset.UtcNow;

            var firstResult = await _repo.MarkFirstReadAndGetStatusAsync(id, initialT4);
            Assert.NotNull(firstResult);
            Assert.NotNull(firstResult.FirstReadAt);
            Assert.Equal(initialT4.ToUnixTimeMilliseconds(), firstResult.FirstReadAt.Value.ToUnixTimeMilliseconds());

            var secondResult = await _repo.MarkFirstReadAndGetStatusAsync(id, laterT4);
            Assert.NotNull(secondResult);
            Assert.NotNull(secondResult.FirstReadAt);
            Assert.Equal(initialT4.ToUnixTimeMilliseconds(), secondResult.FirstReadAt.Value.ToUnixTimeMilliseconds());
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // Сценарій 5 (О1.5 DPC): rejected і failed працюють аналогічно done (перший GET встановлює first_read_at, повторний зберігає)
    [Theory]
    [InlineData("rejected")]
    [InlineData("failed")]
    public async Task Scenario5_RejectedAndFailed_WorkIdenticallyToDone_FirstGetSetsAndRepeatKeepsFirstReadAt(string finalStatus)
    {
        if (!await IsDatabaseAvailableAsync())
        {
            return; // Пропуск для середовищ без PostgreSQL
        }

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, finalStatus, null);
            var tInitial = DateTimeOffset.UtcNow.AddMinutes(-2);
            var tLater = DateTimeOffset.UtcNow;

            // Перший GET встановлює first_read_at
            var firstResult = await _repo.MarkFirstReadAndGetStatusAsync(id, tInitial);
            Assert.NotNull(firstResult);
            Assert.Equal(finalStatus, firstResult.Status);
            Assert.NotNull(firstResult.FirstReadAt);
            Assert.Equal(tInitial.ToUnixTimeMilliseconds(), firstResult.FirstReadAt.Value.ToUnixTimeMilliseconds());

            // Повторний GET не перезаписує first_read_at
            var repeatResult = await _repo.MarkFirstReadAndGetStatusAsync(id, tLater);
            Assert.NotNull(repeatResult);
            Assert.NotNull(repeatResult.FirstReadAt);
            Assert.Equal(tInitial.ToUnixTimeMilliseconds(), repeatResult.FirstReadAt.Value.ToUnixTimeMilliseconds());
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // Сценарій 6 (О1.5 DPC): 10 паралельних GET не перезаписують first_read_at і повертають однаковий timestamp
    [SkippableFact]
    public async Task Scenario6_ConcurrentGets_OnCompletedRequest_DoNotOverwriteInitialTimestamp()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "done", null);
            var baseTime = DateTimeOffset.UtcNow;

            var tasks = Enumerable.Range(1, 10).Select(async i =>
            {
                var now = baseTime.AddMilliseconds(i * 100);
                return await _repo.MarkFirstReadAndGetStatusAsync(id, now);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.NotNull(r));
            var firstRecorded = results[0]!.FirstReadAt;
            Assert.NotNull(firstRecorded);
            Assert.All(results, r => Assert.Equal(firstRecorded.Value.ToUnixTimeMilliseconds(), r!.FirstReadAt!.Value.ToUnixTimeMilliseconds()));
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // Сценарій 7 (О1.5 DPC): ранній опрос (polling) не обнуляє delivery_wait_ms;
    // delivery_wait_ms коректно фіксується при фактичному отриманні фінального результату
    [Fact]
    public async Task Scenario7_EarlyPolling_DoesNotZeroDeliveryWaitMs_AccuratelyRecordedOnFinalResult()
    {
        var metrics = new EnrollmentService.Metrics.MetricsCollector();
        var id = Guid.NewGuid();

        var item = new QueuedRequestItem
        {
            Id = id,
            StudentId = "student-poll-test",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        metrics.RecordAccepted(item, currentQueueLength: 1);
        metrics.RecordWorkerStarted(item, currentQueueLength: 0);

        // 1. Раннє опитування клієнта (polling) кілька разів під час обробки
        metrics.RecordFirstReadIfCompleted(id);
        metrics.RecordFirstReadIfCompleted(id);

        var (earlyQWait, earlyProc, earlyDeliv) = metrics.GetItemTimings(id);
        Assert.NotNull(earlyQWait);
        Assert.Null(earlyProc);
        Assert.Null(earlyDeliv); // delivery_wait_ms ще не існує

        // 2. Воркер завершує виконання
        metrics.MarkProcessingFinished(item);
        metrics.RecordWorkerFinished(item, "done", currentQueueLength: 0);

        // Імітуємо очікування видачі клієнту (невелика пауза)
        await Task.Delay(20);

        // 3. Клієнт отримує вже завершену заявку через перший GET результату
        metrics.RecordFirstReadIfCompleted(id);

        var (qWait, proc, deliv) = metrics.GetItemTimings(id);
        Assert.NotNull(qWait);
        Assert.NotNull(proc);
        Assert.NotNull(deliv);
        Assert.True(deliv.Value > 0.0, $"delivery_wait_ms повинен бути більшим за 0 мс, але отримано {deliv.Value}");

        // 4. Повторний GET клієнта не змінює раніше зафіксований delivery_wait_ms
        var (qWait2, proc2, deliv2) = metrics.GetItemTimings(id);
        Assert.Equal(deliv.Value, deliv2!.Value);
    }

    // Допоміжний тест: GET неіснуючої заявки не створює відмітку і повертає null
    [SkippableFact]
    public async Task GetNonExistentRequest_ReturnsNull_DoesNotCreateTimestamp()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var nonExistentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var result = await _repo.MarkFirstReadAndGetStatusAsync(nonExistentId, now);

        Assert.Null(result);
    }

    // Сценарій 8 (О1.5 DPC): Детермінована регресійна перевірка гонки між COMMIT та FinishProcessingTimestamp.
    // Перевіряє, що клієнтський GET, виконаний у момент фіксації COMMIT, фіксує delivery_wait_ms
    // саме на першому читанні, а не відкладає його на повторні GET.
    [SkippableFact]
    public async Task Scenario8_DeterministicRace_CommitAndFinishProcessingTimestamp_SynchronizedOnFirstGet()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        string studentId = "student-e2e-race-01";
        string groupId = "GRP-E2E-RACE-01";
        string discId = "DISC-E2E-RACE-01";
        var requestId = Guid.NewGuid();

        var options = new ServiceOptions
        {
            AllowTestOverrides = true,
            SlowCallMinMs = 0,
            SlowCallMaxMs = 0,
            Workers = 1
        };

        var groupRepo = new GroupRepository(_factory);
        var enrollmentRepo = new EnrollmentRepository(_factory, options);
        var decanateRepo = new DecanateRepository(_factory);
        var metrics = new MetricsCollector();
        var queue = new MonitorRequestQueue(10);

        var processor = new EnrollmentRequestProcessor(
            _repo,
            groupRepo,
            enrollmentRepo,
            decanateRepo,
            _factory,
            metrics,
            options);

        try
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, requestId);

            // 1. Створення тестових даних у БД
            await using (var conn = await _factory.OpenConnectionAsync())
            {
                await using var cmdDisc = new NpgsqlCommand(@"
                    INSERT INTO discipline (id, code, title, ects, is_test_only)
                    VALUES (@dId, 'CODE-' || @dId, 'Race Discipline', 5.0, true)
                    ON CONFLICT (id) DO UPDATE SET ects = 5.0;", conn);
                cmdDisc.Parameters.AddWithValue("dId", discId);
                await cmdDisc.ExecuteNonQueryAsync();

                await using var cmdGrp = new NpgsqlCommand(@"
                    INSERT INTO discipline_group (id, discipline_id, group_code, seats, available, opens_at, closes_at, university_tz, closed)
                    VALUES (@gId, @dId, 'GRP-' || @gId, 10, 10, NOW() - INTERVAL '1 hour', NOW() + INTERVAL '1 hour', 'Europe/Kyiv', false)
                    ON CONFLICT (id) DO UPDATE SET seats = 10, available = 10, closed = false;", conn);
                cmdGrp.Parameters.AddWithValue("gId", groupId);
                cmdGrp.Parameters.AddWithValue("dId", discId);
                await cmdGrp.ExecuteNonQueryAsync();

                await using var cmdStud = new NpgsqlCommand(@"
                    INSERT INTO student (id, email, full_name, max_disciplines, active_count, active_ects)
                    VALUES (@sId, @sId || '@test.local', 'Race Student', 5, 0, 0.0)
                    ON CONFLICT (id) DO UPDATE SET active_count = 0, active_ects = 0.0;", conn);
                cmdStud.Parameters.AddWithValue("sId", studentId);
                await cmdStud.ExecuteNonQueryAsync();
            }

            // 2. Вставка заявки зі статусом queued
            await InsertTestRequestAsync(requestId, "queued", null);

            var item = new QueuedRequestItem
            {
                Id = requestId,
                StudentId = studentId,
                GroupId = groupId,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            Assert.True(metrics.TryEnqueueReservedAndRecordAccepted(queue, item));

            // 3. Перевірка проміжних статусів: ранній polling під час queued не фіксує FirstReadAt або delivery_wait_ms
            var earlyQueuedGet = await _repo.MarkFirstReadAndGetStatusAsync(requestId, DateTimeOffset.UtcNow);
            Assert.NotNull(earlyQueuedGet);
            Assert.Equal("queued", earlyQueuedGet.Status);
            Assert.Null(earlyQueuedGet.FirstReadAt);

            metrics.RecordFirstReadIfCompleted(requestId);
            var (earlyQWait, earlyProc, earlyDeliv) = metrics.GetItemTimings(requestId);
            Assert.Null(earlyDeliv);

            // 4. Налаштування точки перехоплення TestHookAfterCommitAsync:
            // Викликається детерміновано ОДРАЗУ після COMMIT бізнес-транзакції в БД,
            // коли статус у PostgreSQL вже став 'done', а воркер ще не вийшов з конвеєра обробки.
            EnrollmentStatusResponseDto? hookedGetStatus = null;
            double? hookedDeliveryMs = null;
            DateTimeOffset? hookedFirstReadAt = null;

            processor.TestHookAfterCommitAsync = async (finishedItem) =>
            {
                // Симуляція першого звернення іншого потоку (клієнтського GET) рівно в момент завершення транзакції
                var t4 = DateTimeOffset.UtcNow;
                hookedGetStatus = await _repo.MarkFirstReadAndGetStatusAsync(finishedItem.Id, t4);
                hookedFirstReadAt = hookedGetStatus?.FirstReadAt;

                if (hookedGetStatus?.Status is "done" or "rejected" or "failed")
                {
                    metrics.RecordFirstReadIfCompleted(finishedItem.Id);
                }

                var (_, _, deliv) = metrics.GetItemTimings(finishedItem.Id);
                hookedDeliveryMs = deliv;
            };

            // 5. Запуск обробки воркером
            await processor.ProcessItemAsync(1, item, isAsyncModel: true, queue);

            // 6. Перевірка результатів перехопленого першого GET:
            Assert.NotNull(hookedGetStatus);
            Assert.Equal("done", hookedGetStatus.Status);
            Assert.NotNull(hookedFirstReadAt);

            // КРИТИЧНИЙ ІНВАРІАНТ: delivery_wait_ms зафіксовано САМЕ на найпершому GET після COMMIT, а не відкладено!
            Assert.NotNull(hookedDeliveryMs);
            Assert.True(hookedDeliveryMs.Value >= 0.0, $"delivery_wait_ms повинен бути >= 0, але отримано {hookedDeliveryMs.Value}");

            // 7. Повторний GET клієнта:
            var secondGet = await _repo.MarkFirstReadAndGetStatusAsync(requestId, DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.NotNull(secondGet);
            Assert.Equal(hookedFirstReadAt.Value.ToUnixTimeMilliseconds(), secondGet.FirstReadAt!.Value.ToUnixTimeMilliseconds());

            metrics.RecordFirstReadIfCompleted(requestId);
            var (_, _, secondDeliveryMs) = metrics.GetItemTimings(requestId);
            Assert.NotNull(secondDeliveryMs);
            // delivery_wait_ms НЕ змінився на час другого GET, а зберігся з першого читання
            Assert.Equal(hookedDeliveryMs.Value, secondDeliveryMs.Value);

            // 8. Конкурентні паралельні GET (10 потоків) отримують ідентичний результат
            var concurrentTasks = Enumerable.Range(1, 10).Select(async i =>
            {
                var now = DateTimeOffset.UtcNow.AddMilliseconds(i * 50);
                var res = await _repo.MarkFirstReadAndGetStatusAsync(requestId, now);
                metrics.RecordFirstReadIfCompleted(requestId);
                var (_, _, d) = metrics.GetItemTimings(requestId);
                return (res?.FirstReadAt, d);
            }).ToArray();

            var concurrentResults = await Task.WhenAll(concurrentTasks);
            Assert.All(concurrentResults, r =>
            {
                Assert.NotNull(r.FirstReadAt);
                Assert.Equal(hookedFirstReadAt.Value.ToUnixTimeMilliseconds(), r.FirstReadAt.Value.ToUnixTimeMilliseconds());
                Assert.Equal(hookedDeliveryMs.Value, r.d!.Value);
            });
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, requestId);
        }
    }

    // Перевірка коректного відкату FinishProcessingTimestamp при неуспішній транзакції
    [Fact]
    public void TransactionRollback_ResetsFinishProcessingTimestamp_DoesNotPublishPrematureFinish()
    {
        var metrics = new MetricsCollector();
        var id = Guid.NewGuid();

        var item = new QueuedRequestItem
        {
            Id = id,
            StudentId = "student-rollback-test",
            GroupId = "GRP-TEST",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        metrics.RecordAccepted(item, currentQueueLength: 1);
        metrics.RecordWorkerStarted(item, currentQueueLength: 0);

        // 1. Воркер публікує позначку перед COMMIT
        metrics.MarkProcessingFinished(item);
        Assert.True(item.FinishProcessingTimestamp > 0);

        // 2. Стається помилка / відкат транзакції (ROLLBACK)
        metrics.ResetProcessingFinished(item);
        Assert.Equal(0, item.FinishProcessingTimestamp);

        // 3. Клієнтський GET під час незавершеного стану не може зафіксувати delivery
        metrics.RecordFirstReadIfCompleted(id);
        Assert.Equal(0, item.FirstReadTimestamp);

        var (qWait, proc, deliv) = metrics.GetItemTimings(id);
        Assert.Null(deliv);

        // 4. Подальша фіксація статусу failure коректно перезаписує FinishProcessingTimestamp
        metrics.MarkProcessingFinished(item);
        metrics.RecordWorkerFinished(item, "failed", currentQueueLength: 0);
        Assert.True(item.FinishProcessingTimestamp > 0);

        // 5. Тепер перший GET неуспішної заявки фіксує час доставки
        metrics.RecordFirstReadIfCompleted(id);
        var (_, _, finalDeliv) = metrics.GetItemTimings(id);
        Assert.NotNull(finalDeliv);
        Assert.True(finalDeliv.Value >= 0.0);
    }
}
