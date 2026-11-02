using EnrollmentService;
using EnrollmentService.Repositories;
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
}
