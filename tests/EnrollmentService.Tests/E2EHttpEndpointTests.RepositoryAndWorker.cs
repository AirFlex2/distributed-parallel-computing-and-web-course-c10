using System.Diagnostics;
using EnrollmentService;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services.Processing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class E2EHttpEndpointTests
{
    [SkippableFact]
    public async Task TryInsertQueuedRequest_WhenRequestedEnrollmentIdIsNull_BackfillsFromEnrollmentId()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-fallback-req";
        string groupId = "GRP-E2E-FALLBACK-REQ";
        string discId = "DISC-E2E-FALLBACK-REQ";
        Guid validEnrollmentId = Guid.NewGuid();
        Guid nonExistentEnrollmentId = Guid.NewGuid();
        Guid testRequestId1 = Guid.NewGuid();
        Guid testRequestId2 = Guid.NewGuid();

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, testRequestId1);
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, testRequestId2);
        try
        {
            await EnsureTestStudentAsync(studentId, 5, 0, 0.0m);
            await EnsureTestGroupAsync(groupId, discId, 5, 5, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            await using var conn = await TestEnvironment.OpenConnectionAsync();

            // 1. Створюємо валідний Web-запис
            await using (var insEnr = new NpgsqlCommand(@"
                INSERT INTO enrollment (id, student_id, group_id, discipline_id, status, version, created_at, expires_at)
                VALUES (@id, @sid, @gid, @did, 'held', 1, NOW(), NOW() + INTERVAL '15 minutes');", conn))
            {
                insEnr.Parameters.AddWithValue("id", validEnrollmentId);
                insEnr.Parameters.AddWithValue("sid", studentId);
                insEnr.Parameters.AddWithValue("gid", groupId);
                insEnr.Parameters.AddWithValue("did", discId);
                await insEnr.ExecuteNonQueryAsync();
            }

            var repo = _factory.Services.GetRequiredService<EnrollmentRequestRepository>();

            // 2. Випадок А: Задано валідний EnrollmentId, але RequestedEnrollmentId = null
            var itemWithValidEnrollment = new QueuedRequestItem
            {
                Id = testRequestId1,
                StudentId = studentId,
                GroupId = groupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                EnrollmentId = validEnrollmentId,
                RequestedEnrollmentId = null // Резервне заповнення має зберегти validEnrollmentId
            };

            bool inserted1 = await repo.TryInsertQueuedRequestAsync(itemWithValidEnrollment, conn);
            Assert.True(inserted1);

            await using (var checkCmd1 = new NpgsqlCommand("SELECT enrollment_id, requested_enrollment_id FROM enrollment_requests WHERE id = @id;", conn))
            {
                checkCmd1.Parameters.AddWithValue("id", testRequestId1);
                await using var rdr = await checkCmd1.ExecuteReaderAsync();
                Assert.True(await rdr.ReadAsync());
                Assert.Equal(validEnrollmentId, rdr.GetGuid(0));
                Assert.Equal(validEnrollmentId, rdr.GetGuid(1));
            }

            // 3. Випадок Б: Задано неіснуючий EnrollmentId (FK violation), RequestedEnrollmentId = null
            var itemWithNonExistentEnrollment = new QueuedRequestItem
            {
                Id = testRequestId2,
                StudentId = studentId,
                GroupId = groupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                EnrollmentId = nonExistentEnrollmentId,
                RequestedEnrollmentId = null // Fallback при FK violation має зберегти nonExistentEnrollmentId у requested_enrollment_id
            };

            bool inserted2 = await repo.TryInsertQueuedRequestAsync(itemWithNonExistentEnrollment, conn);
            Assert.True(inserted2);

            await using (var checkCmd2 = new NpgsqlCommand("SELECT enrollment_id, requested_enrollment_id FROM enrollment_requests WHERE id = @id;", conn))
            {
                checkCmd2.Parameters.AddWithValue("id", testRequestId2);
                await using var rdr = await checkCmd2.ExecuteReaderAsync();
                Assert.True(await rdr.ReadAsync());
                Assert.True(rdr.IsDBNull(0), "enrollment_id має стати NULL при порушенні FK");
                Assert.Equal(nonExistentEnrollmentId, rdr.GetGuid(1)); // вихідний ID не втрачено!
            }
        }
        finally
        {
            Exception? cleanupEx = null;
            try
            {
                await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, testRequestId1);
            }
            catch (Exception ex)
            {
                cleanupEx = CombineExceptions(cleanupEx, ex);
            }

            try
            {
                await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, testRequestId2);
            }
            catch (Exception ex)
            {
                cleanupEx = CombineExceptions(cleanupEx, ex);
            }

            if (cleanupEx != null)
            {
                throw cleanupEx;
            }
        }
    }

    [Fact]
    public async Task RegisterPreProcessingHook_FullLifecycle_TriggersOnlyForTargetAndUnregistersOnDispose()
    {
        var processor = _factory.Services.GetRequiredService<EnrollmentRequestProcessor>();
        Guid targetRequestId = Guid.NewGuid();
        Guid otherRequestId = Guid.NewGuid();
        int targetHookCallCount = 0;
        int otherHookCallCount = 0;

        var fakeQueue = new MonitorRequestQueue(10);
        var otherItem = new QueuedRequestItem
        {
            Id = otherRequestId,
            StudentId = "student-hook-lifecycle-other",
            GroupId = "GRP-HOOK-LIFECYCLE",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var targetItem = new QueuedRequestItem
        {
            Id = targetRequestId,
            StudentId = "student-hook-lifecycle-target",
            GroupId = "GRP-HOOK-LIFECYCLE",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        // 1. Зареєструвати hook для targetRequestId
        var hookReg = processor.RegisterPreProcessingHook(targetRequestId, item =>
        {
            if (item.Id == targetRequestId)
            {
                Interlocked.Increment(ref targetHookCallCount);
            }
            else
            {
                Interlocked.Increment(ref otherHookCallCount);
            }
            return Task.CompletedTask;
        });

        try
        {
            // 2. Передати в ProcessItemAsync запит з іншим ID
            await processor.ProcessItemAsync(1, otherItem, isAsyncModel: false, fakeQueue, CancellationToken.None);

            // 3. Перевірити, що hook не викликаний
            Assert.Equal(0, targetHookCallCount);
            Assert.Equal(0, otherHookCallCount);

            // 4. Передати запит з targetRequestId
            await processor.ProcessItemAsync(1, targetItem, isAsyncModel: false, fakeQueue, CancellationToken.None);

            // 5. Перевірити, що hook викликано ровно один раз
            Assert.Equal(1, targetHookCallCount);
            Assert.Equal(0, otherHookCallCount);

            // 6. Звільнити реєстрацію через Dispose
            hookReg.Dispose();

            // 7. Повторно обробити запит з targetRequestId
            await processor.ProcessItemAsync(1, targetItem, isAsyncModel: false, fakeQueue, CancellationToken.None);

            // 8. Перевірити, що hook більше не викликається (лічильник залишився 1)
            Assert.Equal(1, targetHookCallCount);
            Assert.Equal(0, otherHookCallCount);
        }
        finally
        {
            hookReg.Dispose();
        }
    }

    [Fact]
    public void RegisterPreProcessingHook_DuplicateRegistrationForSameId_ThrowsInvalidOperationException()
    {
        var processor = _factory.Services.GetRequiredService<EnrollmentRequestProcessor>();
        Guid requestId = Guid.NewGuid();

        using var reg1 = processor.RegisterPreProcessingHook(requestId, _ => Task.CompletedTask);

        // Спроба повторної реєстрації для того самого requestId повинна викликати InvalidOperationException (захист від перезапису)
        var ex = Assert.Throws<InvalidOperationException>(() =>
            processor.RegisterPreProcessingHook(requestId, _ => Task.CompletedTask));

        Assert.Contains(requestId.ToString(), ex.Message);
    }

    [SkippableFact]
    public async Task DecanateRoster_DuplicateDisciplineIndex_EnforcesUniqueness()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-roster-dup-d";
        string groupId = "GRP-ROSTER-DUP-D";
        string discId = "DISC-ROSTER-DUP-D";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);

            var decanateRepo = _factory.Services.GetRequiredService<DecanateRepository>();
            var reqRepo = _factory.Services.GetRequiredService<EnrollmentRequestRepository>();

            var reqId1 = Guid.NewGuid();
            var reqId2 = Guid.NewGuid();

            await using var conn = await TestEnvironment.OpenConnectionAsync();

            // Вставляємо дві заявки в enrollment_requests
            var item1 = new QueuedRequestItem { Id = reqId1, StudentId = studentId, GroupId = groupId, CreatedAtUtc = DateTimeOffset.UtcNow };
            var item2 = new QueuedRequestItem { Id = reqId2, StudentId = studentId, GroupId = groupId, CreatedAtUtc = DateTimeOffset.UtcNow };
            await reqRepo.TryInsertQueuedRequestAsync(item1, conn);
            await reqRepo.TryInsertQueuedRequestAsync(item2, conn);

            // Перший запис у відомість деканату успішний
            await decanateRepo.InsertRosterEntryAsync(reqId1, studentId, groupId, discId, 4.0m, null, "primary_roster", DateTimeOffset.UtcNow, conn);

            // Другий запис для того самого студента і тієї самої дисципліни викидає DecanateConflictException (uq_decanate_roster_student_discipline)
            var ex = await Assert.ThrowsAsync<DecanateConflictException>(async () =>
            {
                await decanateRepo.InsertRosterEntryAsync(reqId2, studentId, groupId, discId, 4.0m, null, "primary_roster", DateTimeOffset.UtcNow, conn);
            });

            Assert.Equal("duplicate_discipline", ex.ConflictType);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task DecanateRoster_DuplicateEnrollmentIndex_EnforcesUniqueness()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-roster-dup-e";
        string groupId = "GRP-ROSTER-DUP-E";
        string discId = "DISC-ROSTER-DUP-E";
        string discId2 = "DISC-ROSTER-DUP-E2";
        string groupId2 = "GRP-ROSTER-DUP-E2";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId2);

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);
            await EnsureTestGroupAsync(groupId2, discId2, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);

            var decanateRepo = _factory.Services.GetRequiredService<DecanateRepository>();
            var reqRepo = _factory.Services.GetRequiredService<EnrollmentRequestRepository>();

            var reqId1 = Guid.NewGuid();
            var reqId2 = Guid.NewGuid();

            await using var conn = await TestEnvironment.OpenConnectionAsync();

            var item1 = new QueuedRequestItem { Id = reqId1, StudentId = studentId, GroupId = groupId, CreatedAtUtc = DateTimeOffset.UtcNow };
            var item2 = new QueuedRequestItem { Id = reqId2, StudentId = studentId, GroupId = groupId2, CreatedAtUtc = DateTimeOffset.UtcNow };
            await reqRepo.TryInsertQueuedRequestAsync(item1, conn);
            await reqRepo.TryInsertQueuedRequestAsync(item2, conn);

            // Створюємо Web-запис
            var enrollmentId = Guid.NewGuid();
            await using (var insCmd = new NpgsqlCommand(@"
                INSERT INTO enrollment (id, student_id, group_id, discipline_id, status, version, created_at, expires_at)
                VALUES (@id, @sid, @gid, @did, 'held', 1, NOW(), NOW() + INTERVAL '15 minutes');", conn))
            {
                insCmd.Parameters.AddWithValue("id", enrollmentId);
                insCmd.Parameters.AddWithValue("sid", studentId);
                insCmd.Parameters.AddWithValue("gid", groupId);
                insCmd.Parameters.AddWithValue("did", discId);
                await insCmd.ExecuteNonQueryAsync();
            }

            // Перша прив'язка enrollmentId у відомості
            await decanateRepo.InsertRosterEntryAsync(reqId1, studentId, groupId, discId, 4.0m, enrollmentId, "joint_web_enrollment", DateTimeOffset.UtcNow, conn);

            // Спроба прив'язати той самий enrollmentId вдруге викидає DecanateConflictException (uq_decanate_roster_enrollment_id)
            var ex = await Assert.ThrowsAsync<DecanateConflictException>(async () =>
            {
                await decanateRepo.InsertRosterEntryAsync(reqId2, studentId, groupId2, discId2, 4.0m, enrollmentId, "joint_web_enrollment", DateTimeOffset.UtcNow, conn);
            });

            Assert.Equal("duplicate_enrollment", ex.ConflictType);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId2);
        }
    }

    [SkippableFact]
    public async Task Worker_HookThrowsAfterCommit_RequestRemainsDone_AndMetricsIntact()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "stud-hook-throw";
        string groupId = "GRP-HOOK-THROW";
        string discId = "DISC-HOOK-THROW";
        Guid reqId = Guid.NewGuid();

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, reqId);

        var connFactory = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        var groupRepo = _factory.Services.GetRequiredService<GroupRepository>();
        var requestRepo = _factory.Services.GetRequiredService<EnrollmentRequestRepository>();
        var enrollmentRepo = _factory.Services.GetRequiredService<EnrollmentRepository>();
        var decanateRepo = _factory.Services.GetRequiredService<DecanateRepository>();
        var metrics = _factory.Services.GetRequiredService<MetricsCollector>();
        var options = _factory.Services.GetRequiredService<ServiceOptions>();
        var testOptions = new ServiceOptions
        {
            InstanceId = options.InstanceId,
            DbConnectionString = options.DbConnectionString,
            ApiAuthToken = options.ApiAuthToken,
            QueueCapacity = options.QueueCapacity,
            Workers = options.Workers,
            UnsafeMode = options.UnsafeMode,
            ExecutionModel = options.ExecutionModel,
            QueueEngine = options.QueueEngine,
            SlowCallMinMs = options.SlowCallMinMs,
            SlowCallMaxMs = options.SlowCallMaxMs,
            AllowTestOverrides = true,
            EnableWorkers = options.EnableWorkers
        };

        var processor = new EnrollmentRequestProcessor(
            requestRepo,
            groupRepo,
            enrollmentRepo,
            decanateRepo,
            connFactory,
            metrics,
            testOptions);

        bool hookCalled = false;
        // Налаштовуємо тестовий hook, що викидає виключення після фіксації транзакції
        processor.TestHookAfterCommitAsync = item =>
        {
            hookCalled = true;
            throw new InvalidOperationException("Simulated exception after transaction COMMIT");
        };

        long initialDone = metrics.TotalDone;
        long initialFailed = metrics.TotalFailed;

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            var item = new QueuedRequestItem
            {
                Id = reqId,
                StudentId = studentId,
                GroupId = groupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                EnqueueTimestamp = Stopwatch.GetTimestamp()
            };

            await using (var conn = await connFactory.OpenConnectionAsync())
            {
                bool inserted = await requestRepo.TryInsertQueuedRequestAsync(item, conn);
                Assert.True(inserted);
            }

            var dummyQueue = new MonitorRequestQueue(100);
            dummyQueue.TryReserveSlot();
            bool enqueued = metrics.TryEnqueueReservedAndRecordAccepted(dummyQueue, item);
            Assert.True(enqueued);

            // Виконуємо обробку воркером
            await processor.ProcessItemAsync(1, item, false, dummyQueue, CancellationToken.None);

            // Перевіряємо, що hook дійсно був викликаний і викинув виключення
            Assert.True(hookCalled, "TestHookAfterCommitAsync мав бути обов'язково викликаний після COMMIT");

            // Перевіряємо статус у базі даних: повинен залишатися 'done', а не 'failed'!
            var statusDto = await requestRepo.GetStatusByIdAsync(reqId);
            Assert.NotNull(statusDto);
            Assert.Equal("done", statusDto.Status);
            Assert.Null(statusDto.RejectionReason);
            Assert.True(statusDto.RecordedInRoster);

            // Перевіряємо метрики: лічильник успішних зафіксованих операцій збільшено, помилок немає
            Assert.Equal(initialDone + 1, metrics.TotalDone);
            Assert.Equal(initialFailed, metrics.TotalFailed);
            var (_, procWait, _) = metrics.GetItemTimings(reqId);
            Assert.NotNull(procWait);
            Assert.True(procWait >= 0, "Час обробки має бути зафіксований у метриках");
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, reqId);
        }
    }

    [SkippableFact]
    public async Task ConfirmEnrollmentFromRoster_VerificationOrder_Tests()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "stud-conf-roster";
        string groupId = "GRP-CONF-ROSTER";
        string discId = "DISC-CONF-ROSTER";
        Guid enrollmentId = Guid.NewGuid();
        Guid reqId = Guid.NewGuid();
        Guid foreignReqId = Guid.NewGuid();

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, reqId);
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, foreignReqId);

        var connFactory = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        var decanateRepo = _factory.Services.GetRequiredService<DecanateRepository>();
        var enrollmentService = _factory.Services.GetRequiredService<EnrollmentService.Services.EnrollmentService>();

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            // 1. Створюємо запис enrollment у стані held
            await using (var conn = await connFactory.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO enrollment (id, student_id, group_id, discipline_id, status, version, created_at, expires_at)
                    VALUES (@eid, @sid, @gid, @did, 'held', 1, NOW(), NOW() + INTERVAL '15 minutes');", conn);
                cmd.Parameters.AddWithValue("eid", enrollmentId);
                cmd.Parameters.AddWithValue("sid", studentId);
                cmd.Parameters.AddWithValue("gid", groupId);
                cmd.Parameters.AddWithValue("did", discId);
                await cmd.ExecuteNonQueryAsync();

                // Створюємо заявку РПО
                await using var cmdReq = new NpgsqlCommand(@"
                    INSERT INTO enrollment_requests (id, student_id, group_id, status, created_at, enrollment_id)
                    VALUES (@rid, @sid, @gid, 'done', NOW(), @eid);", conn);
                cmdReq.Parameters.AddWithValue("rid", reqId);
                cmdReq.Parameters.AddWithValue("sid", studentId);
                cmdReq.Parameters.AddWithValue("gid", groupId);
                cmdReq.Parameters.AddWithValue("eid", enrollmentId);
                await cmdReq.ExecuteNonQueryAsync();
            }

            // Перевірка 1: Відсутність у decanate_roster -> ConfirmEnrollmentFromRosterAsync повертає FALSE
            bool missingRosterResult = await enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, reqId);
            Assert.False(missingRosterResult, "Якщо запис відсутній у decanate_roster, підтвердження має повертати false");

            // Тепер вносимо запис до decanate_roster з reqId
            await using (var conn = await connFactory.OpenConnectionAsync())
            {
                await decanateRepo.InsertRosterEntryAsync(reqId, studentId, groupId, discId, 5.0m, enrollmentId, "joint_web_enrollment", DateTimeOffset.UtcNow, conn);
            }

            // Перевірка 2: Чужий requestId -> ConfirmEnrollmentFromRosterAsync повертає FALSE
            bool foreignReqResult = await enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, foreignReqId);
            Assert.False(foreignReqResult, "Чужий requestId повинен відхилятися з false");

            // Перевірка 3: Коректне первинне підтвердження з held -> confirmed
            bool firstConfirm = await enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, reqId);
            Assert.True(firstConfirm, "Коректний запис із відомості повинен бути успішно підтверджений");

            // Перевіряємо версію в базі даних
            int versionAfterFirstConfirm;
            await using (var conn = await connFactory.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("SELECT version FROM enrollment WHERE id = @eid;", conn);
                cmd.Parameters.AddWithValue("eid", enrollmentId);
                versionAfterFirstConfirm = (int)(await cmd.ExecuteScalarAsync())!;
            }
            Assert.Equal(2, versionAfterFirstConfirm);

            // Перевірка 4: Ідемпотентний повтор підтвердження з тим же reqId для вже confirmed
            // Повертає true, але версія НЕ змінюється
            bool repeatConfirm = await enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, reqId);
            Assert.True(repeatConfirm, "Ідемпотентний повтор підтвердження повинен повертати true");

            await using (var conn = await connFactory.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("SELECT version FROM enrollment WHERE id = @eid;", conn);
                cmd.Parameters.AddWithValue("eid", enrollmentId);
                int versionAfterRepeat = (int)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal(versionAfterFirstConfirm, versionAfterRepeat);
            }

            // Перевірка 5: Чужий requestId для вже підтвердженого confirmed запису -> повертає FALSE!
            bool foreignOnConfirmed = await enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, foreignReqId);
            Assert.False(foreignOnConfirmed, "Чужий requestId навіть для confirmed запису повинен повертати false");

            // Перевірка 6: Невалідний стан cancelled -> повертає FALSE
            await using (var conn = await connFactory.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("UPDATE enrollment SET status = 'cancelled' WHERE id = @eid;", conn);
                cmd.Parameters.AddWithValue("eid", enrollmentId);
                await cmd.ExecuteNonQueryAsync();
            }

            bool cancelledResult = await enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, reqId);
            Assert.False(cancelledResult, "Скасований запис (cancelled) повинен відхилятися з false");

            // Перевірка 7: Невалідний стан expired -> повертає FALSE
            await using (var conn = await connFactory.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("UPDATE enrollment SET status = 'expired' WHERE id = @eid;", conn);
                cmd.Parameters.AddWithValue("eid", enrollmentId);
                await cmd.ExecuteNonQueryAsync();
            }

            bool expiredResult = await enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, reqId);
            Assert.False(expiredResult, "Прострочений запис (expired) повинен відхилятися з false");

            // Перевірка 8: Запис held, але час резерву сплив (expires_at <= now), проте запис уже внесено до відомості decanate_roster -> повертає TRUE
            await using (var conn = await connFactory.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("UPDATE enrollment SET status = 'held', created_at = NOW() - INTERVAL '20 minutes', expires_at = NOW() - INTERVAL '5 minutes' WHERE id = @eid;", conn);
                cmd.Parameters.AddWithValue("eid", enrollmentId);
                await cmd.ExecuteNonQueryAsync();
            }

            bool timedOutHeldResult = await enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, reqId);
            Assert.True(timedOutHeldResult, "Запис held із минулим expires_at, внесений до відомості decanate_roster, повинен підтверджуватися з true");
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, reqId);
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, foreignReqId);
        }
    }
}
