using System.Text;
using EnrollmentService.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class E2EHttpEndpointTests
{
    [SkippableFact]
    public async Task Migration007_DisciplineGroups_DpcSandboxEnabled_FlagConfiguration()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        await using var conn = await TestEnvironment.OpenConnectionAsync();

        // 1. Штатні групи автономного контуру РПО (міграція 007) мають мати dpc_sandbox_enabled = TRUE
        string[] dpcAllowedGroups = { "DISC-DPC-01", "DISC-TEST-SAFE" };
        foreach (var grpId in dpcAllowedGroups)
        {
            await using var cmd = new NpgsqlCommand("SELECT dpc_sandbox_enabled FROM discipline_group WHERE id = @id;", conn);
            cmd.Parameters.AddWithValue("id", grpId);
            var result = await cmd.ExecuteScalarAsync();
            Assert.NotNull(result);
            Assert.True((bool)result, $"Група '{grpId}' повинна мати dpc_sandbox_enabled = TRUE згідно з міграцією 007.");
        }

        // 2. Штатні групи інтерактивного Web-контуру (міграції 002-005) мають мати dpc_sandbox_enabled = FALSE
        string[] webOnlyGroups = { "DISC-WEB-01", "DISC-AI-02", "DISC-SEC-03", "DISC-SEC-04", "DISC-DB-01", "DISC-ONE-SEAT", "DISC-WINDOW-TEST", "DISC-ECTS-10-G1", "DISC-ECTS-10-5-G1" };
        foreach (var grpId in webOnlyGroups)
        {
            await using var cmd = new NpgsqlCommand("SELECT dpc_sandbox_enabled FROM discipline_group WHERE id = @id;", conn);
            cmd.Parameters.AddWithValue("id", grpId);
            var result = await cmd.ExecuteScalarAsync();
            Assert.NotNull(result);
            Assert.False((bool)result, $"Штатна Web-група '{grpId}' повинна мати dpc_sandbox_enabled = FALSE згідно з міграцією 007.");
        }
    }

    [SkippableFact]
    public async Task Migration008_Schema_EnrollmentIdempotencyAndJointSupport()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        await using var conn = await TestEnvironment.OpenConnectionAsync();

        // 1. Перевіряємо наявність колонок idempotency_key та request_payload_hash у таблиці enrollment
        await using var cmd1 = new NpgsqlCommand(@"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_name = 'enrollment' AND column_name IN ('idempotency_key', 'request_payload_hash');", conn);
        var cols1 = new HashSet<string>();
        await using (var rdr = await cmd1.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
            {
                cols1.Add(rdr.GetString(0));
            }
        }
        Assert.Contains("idempotency_key", cols1);
        Assert.Contains("request_payload_hash", cols1);

        // 2. Перевіряємо наявність колонки enrollment_id у таблиці enrollment_requests
        await using var cmd2 = new NpgsqlCommand(@"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_name = 'enrollment_requests' AND column_name = 'enrollment_id';", conn);
        var res2 = await cmd2.ExecuteScalarAsync();
        Assert.NotNull(res2);
        Assert.Equal("enrollment_id", (string)res2);

        // 3. Перевіряємо наявність глобального часткового унікального індексу idx_enrollment_idempotency_global та предикату
        await using var cmd3 = new NpgsqlCommand(@"
            SELECT indexdef
            FROM pg_indexes
            WHERE tablename = 'enrollment' AND indexname = 'idx_enrollment_idempotency_global';", conn);
        string? indexDef = (string?)await cmd3.ExecuteScalarAsync();
        Assert.NotNull(indexDef);
        Assert.Contains("UNIQUE", indexDef, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotency_key IS NOT NULL", indexDef, StringComparison.OrdinalIgnoreCase);

        // 4. Перевіряємо зовнішній ключ enrollment_requests.enrollment_id -> enrollment(id)
        await using var cmd4 = new NpgsqlCommand(@"
            SELECT confdeltype
            FROM pg_constraint c
            JOIN pg_class t ON c.conrelid = t.oid
            WHERE t.relname = 'enrollment_requests' AND c.contype = 'f'
              AND EXISTS (
                  SELECT 1 FROM pg_attribute a
                  WHERE a.attrelid = t.oid AND a.attnum = ANY(c.conkey) AND a.attname = 'enrollment_id'
              );", conn);
        var delRuleObj = await cmd4.ExecuteScalarAsync();
        Assert.NotNull(delRuleObj);
        char delRule = Convert.ToChar(delRuleObj);
        Assert.Equal('n', delRule); // 'n' stands for SET NULL in PostgreSQL pg_constraint

        // 5. Перевіряємо функціональну поведінку ON DELETE SET NULL
        string testStudent = "student-e2e-m008-test";
        string testGroup = "GRP-E2E-M008-G";
        string testDisc = "DISC-E2E-M008-D";
        Guid testEnrollmentId = Guid.NewGuid();
        Guid testRequestId = Guid.NewGuid();

        await TestEnvironment.CleanupTestDataAsync(testStudent, testGroup, testDisc, testRequestId);
        try
        {
            await EnsureTestStudentAsync(testStudent, 5, 0, 0.0m);
            await EnsureTestGroupAsync(testGroup, testDisc, 5, 5, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // Вставляємо тестовий запис enrollment
            await using (var insEnr = new NpgsqlCommand(@"
                INSERT INTO enrollment (id, student_id, group_id, discipline_id, status, version, created_at, expires_at)
                VALUES (@id, @sid, @gid, @did, 'held', 1, NOW(), NOW() + INTERVAL '15 minutes');", conn))
            {
                insEnr.Parameters.AddWithValue("id", testEnrollmentId);
                insEnr.Parameters.AddWithValue("sid", testStudent);
                insEnr.Parameters.AddWithValue("gid", testGroup);
                insEnr.Parameters.AddWithValue("did", testDisc);
                await insEnr.ExecuteNonQueryAsync();
            }

            // Вставляємо заявку enrollment_requests з посиланням на цей enrollment_id
            await using (var insReq = new NpgsqlCommand(@"
                INSERT INTO enrollment_requests (id, student_id, group_id, status, created_at, enrollment_id)
                VALUES (@reqId, @sid, @gid, 'queued', NOW(), @enrId);", conn))
            {
                insReq.Parameters.AddWithValue("reqId", testRequestId);
                insReq.Parameters.AddWithValue("sid", testStudent);
                insReq.Parameters.AddWithValue("gid", testGroup);
                insReq.Parameters.AddWithValue("enrId", testEnrollmentId);
                await insReq.ExecuteNonQueryAsync();
            }

            // Видаляємо запис enrollment
            await using (var delEnr = new NpgsqlCommand("DELETE FROM enrollment WHERE id = @id;", conn))
            {
                delEnr.Parameters.AddWithValue("id", testEnrollmentId);
                await delEnr.ExecuteNonQueryAsync();
            }

            // Перевіряємо, що в enrollment_requests значення enrollment_id автоматично обнулилося (SET NULL)
            await using (var checkReq = new NpgsqlCommand("SELECT enrollment_id FROM enrollment_requests WHERE id = @reqId;", conn))
            {
                checkReq.Parameters.AddWithValue("reqId", testRequestId);
                var val = await checkReq.ExecuteScalarAsync();
                Assert.True(val == null || val == DBNull.Value, "Очікувалося обнулення enrollment_id при каскадному ON DELETE SET NULL");
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(testStudent, testGroup, testDisc, testRequestId);
        }
    }

    [SkippableFact]
    public async Task Migration009_Schema_RequestedEnrollmentId()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        await using var conn = await TestEnvironment.OpenConnectionAsync();

        // 1. Перевіряємо наявність колонки requested_enrollment_id у таблиці enrollment_requests
        await using var cmd = new NpgsqlCommand(@"
            SELECT column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_name = 'enrollment_requests' AND column_name = 'requested_enrollment_id';", conn);
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync(), "Колонка requested_enrollment_id відсутня в таблиці enrollment_requests");
            Assert.Equal("requested_enrollment_id", reader.GetString(0));
            Assert.Equal("uuid", reader.GetString(1));
            Assert.Equal("YES", reader.GetString(2));
        }

        // 2. Перевіряємо, що requested_enrollment_id НЕ має обмеження зовнішнього ключа (FK)
        await using var cmdFk = new NpgsqlCommand(@"
            SELECT count(*)
            FROM pg_constraint c
            JOIN pg_class t ON c.conrelid = t.oid
            WHERE t.relname = 'enrollment_requests' AND c.contype = 'f'
              AND EXISTS (
                  SELECT 1 FROM pg_attribute a
                  WHERE a.attrelid = t.oid AND a.attnum = ANY(c.conkey) AND a.attname = 'requested_enrollment_id'
              );", conn);
        long fkCount = (long)(await cmdFk.ExecuteScalarAsync())!;
        Assert.Equal(0L, fkCount);

        // 3. Перевіряємо поведінку: при наявності enrollment і подальшому DELETE,
        // enrollment_id стає NULL (каскадно), а requested_enrollment_id залишається незмінним
        string testStudent = "student-e2e-m009-test";
        string testGroup = "GRP-E2E-M009-G";
        string testDisc = "DISC-E2E-M009-D";
        Guid testEnrollmentId = Guid.NewGuid();
        Guid testRequestId = Guid.NewGuid();

        await TestEnvironment.CleanupTestDataAsync(testStudent, testGroup, testDisc, testRequestId);
        try
        {
            await EnsureTestStudentAsync(testStudent, 5, 0, 0.0m);
            await EnsureTestGroupAsync(testGroup, testDisc, 5, 5, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // Вставляємо тестовий запис enrollment
            await using (var insEnr = new NpgsqlCommand(@"
                INSERT INTO enrollment (id, student_id, group_id, discipline_id, status, version, created_at, expires_at)
                VALUES (@id, @sid, @gid, @did, 'held', 1, NOW(), NOW() + INTERVAL '15 minutes');", conn))
            {
                insEnr.Parameters.AddWithValue("id", testEnrollmentId);
                insEnr.Parameters.AddWithValue("sid", testStudent);
                insEnr.Parameters.AddWithValue("gid", testGroup);
                insEnr.Parameters.AddWithValue("did", testDisc);
                await insEnr.ExecuteNonQueryAsync();
            }

            // Вставляємо заявку через репозиторій
            var reqRepo = _factory.Services.GetRequiredService<EnrollmentRequestRepository>();
            var queuedItem = new QueuedRequestItem
            {
                Id = testRequestId,
                StudentId = testStudent,
                GroupId = testGroup,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                EnrollmentId = testEnrollmentId,
                RequestedEnrollmentId = testEnrollmentId
            };
            bool inserted = await reqRepo.TryInsertQueuedRequestAsync(queuedItem, conn);
            Assert.True(inserted);

            // Видаляємо запис enrollment
            await using (var delEnr = new NpgsqlCommand("DELETE FROM enrollment WHERE id = @id;", conn))
            {
                delEnr.Parameters.AddWithValue("id", testEnrollmentId);
                await delEnr.ExecuteNonQueryAsync();
            }

            // Перевіряємо в базі: enrollment_id став NULL, але requested_enrollment_id зберігся
            await using (var checkReq = new NpgsqlCommand(@"
                SELECT enrollment_id, requested_enrollment_id
                FROM enrollment_requests
                WHERE id = @reqId;", conn))
            {
                checkReq.Parameters.AddWithValue("reqId", testRequestId);
                await using var reader = await checkReq.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0), "enrollment_id мав бути обнулений через ON DELETE SET NULL");
                Assert.Equal(testEnrollmentId, reader.GetGuid(1));
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(testStudent, testGroup, testDisc, testRequestId);
        }
    }

    [SkippableFact]
    public async Task Migration009_DataBackfill_PreservesExistingEnrollmentIdsAndHandlesNulls()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-m009-bf";
        string groupId = "GRP-E2E-M009-BF";
        string discId = "DISC-E2E-M009-BF";
        Guid heldEnrollmentId = Guid.NewGuid();
        Guid reqWithEnrollmentId = Guid.NewGuid();
        Guid reqWithNullEnrollmentId = Guid.NewGuid();
        string tempSchema = $"test_mig009_{Guid.NewGuid():N}";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, reqWithEnrollmentId);
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, reqWithNullEnrollmentId);

        // Відкриваємо виділене непопульоване (Pooling=false) з'єднання для тесту міграції.
        // Це гарантує, що змінений search_path фізично закриється разом із сокетом
        // і ніколи не потрапить у пул з'єднань інших тестів або компонентів сервісу.
        var nonPooledBuilder = new NpgsqlConnectionStringBuilder(TestEnvironment.ConnectionString)
        {
            Pooling = false
        };
        await using var conn = new NpgsqlConnection(nonPooledBuilder.ConnectionString);
        await conn.OpenAsync();

        Exception? testException = null;
        try
        {
            await EnsureTestStudentAsync(studentId, 5, 0, 0.0m);
            await EnsureTestGroupAsync(groupId, discId, 5, 5, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // 1. Створюємо ізольовану тимчасову схему та розгортаємо таблицю enrollment_requests
            //    у стані ДО застосування міграції 009 (стовпчик enrollment_id існує, а requested_enrollment_id ВІДСУТНІЙ).
            await using (var cmdSetup = new NpgsqlCommand($@"
                CREATE SCHEMA ""{tempSchema}"";
                SET search_path = ""{tempSchema}"", public;
                CREATE TABLE enrollment_requests (
                    id UUID PRIMARY KEY,
                    student_id TEXT NOT NULL REFERENCES student(id) ON DELETE CASCADE,
                    group_id TEXT NOT NULL REFERENCES discipline_group(id) ON DELETE CASCADE,
                    assigned_group_id TEXT NULL REFERENCES discipline_group(id) ON DELETE SET NULL,
                    status TEXT NOT NULL CHECK (status IN ('queued', 'processing', 'done', 'rejected', 'failed')),
                    rejection_reason TEXT NULL,
                    created_at TIMESTAMPTZ NOT NULL,
                    started_at TIMESTAMPTZ NULL,
                    finished_at TIMESTAMPTZ NULL,
                    first_read_at TIMESTAMPTZ NULL,
                    idempotency_key TEXT NULL,
                    enrollment_id UUID NULL REFERENCES enrollment(id) ON DELETE SET NULL
                );", conn))
            {
                await cmdSetup.ExecuteNonQueryAsync();
            }

            // 2. Переконуємося, що в новій тестовій схемі стовпчик requested_enrollment_id дійсно відсутній перед міграцією
            await using (var checkColBefore = new NpgsqlCommand(@"
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = 'enrollment_requests' AND column_name = 'requested_enrollment_id';", conn))
            {
                checkColBefore.Parameters.AddWithValue("schema", tempSchema);
                long countBefore = (long)(await checkColBefore.ExecuteScalarAsync())!;
                Assert.Equal(0L, countBefore);
            }

            // 3. Створюємо валідний Web-запис у публічній схемі для забезпечення зв'язку FK
            await using (var insEnr = new NpgsqlCommand(@"
                INSERT INTO public.enrollment (id, student_id, group_id, discipline_id, status, version, created_at, expires_at)
                VALUES (@id, @sid, @gid, @did, 'held', 1, NOW(), NOW() + INTERVAL '15 minutes');", conn))
            {
                insEnr.Parameters.AddWithValue("id", heldEnrollmentId);
                insEnr.Parameters.AddWithValue("sid", studentId);
                insEnr.Parameters.AddWithValue("gid", groupId);
                insEnr.Parameters.AddWithValue("did", discId);
                await insEnr.ExecuteNonQueryAsync();
            }

            // 4. Створюємо запис із заповненим enrollment_id (до міграції 009)
            await using (var insPre1 = new NpgsqlCommand(@"
                INSERT INTO enrollment_requests (id, student_id, group_id, status, created_at, enrollment_id)
                VALUES (@id, @sid, @gid, 'queued', NOW(), @enrId);", conn))
            {
                insPre1.Parameters.AddWithValue("id", reqWithEnrollmentId);
                insPre1.Parameters.AddWithValue("sid", studentId);
                insPre1.Parameters.AddWithValue("gid", groupId);
                insPre1.Parameters.AddWithValue("enrId", heldEnrollmentId);
                await insPre1.ExecuteNonQueryAsync();
            }

            // 5. Створюємо запис із enrollment_id = NULL (до міграції 009)
            await using (var insPre2 = new NpgsqlCommand(@"
                INSERT INTO enrollment_requests (id, student_id, group_id, status, created_at, enrollment_id)
                VALUES (@id, @sid, @gid, 'queued', NOW(), NULL);", conn))
            {
                insPre2.Parameters.AddWithValue("id", reqWithNullEnrollmentId);
                insPre2.Parameters.AddWithValue("sid", studentId);
                insPre2.Parameters.AddWithValue("gid", groupId);
                await insPre2.ExecuteNonQueryAsync();
            }

            // 6. Завантажуємо та виконуємо безпосередньо SQL-скрипт міграції 009 на ізольованій схемі
            string migrationFile = FindMigrationFile("009_add_requested_enrollment_id.sql");
            string migrationSql = await File.ReadAllTextAsync(migrationFile, Encoding.UTF8);

            await using (var cmdMigrate = new NpgsqlCommand(migrationSql, conn))
            {
                await cmdMigrate.ExecuteNonQueryAsync();
            }

            // 7. Перевіряємо, що новий стовпчик дійсно з'явився у схемі після виконання міграції
            await using (var checkColAfter = new NpgsqlCommand(@"
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = 'enrollment_requests' AND column_name = 'requested_enrollment_id';", conn))
            {
                checkColAfter.Parameters.AddWithValue("schema", tempSchema);
                long countAfter = (long)(await checkColAfter.ExecuteScalarAsync())!;
                Assert.Equal(1L, countAfter);
            }

            // 8. Перевіряємо запис з непорожнім enrollment_id:
            // - перенесення колишнього enrollment_id в requested_enrollment_id підтверджено
            // - вихідний enrollment_id не був змінений
            await using (var check1 = new NpgsqlCommand(@"
                SELECT enrollment_id, requested_enrollment_id
                FROM enrollment_requests
                WHERE id = @id;", conn))
            {
                check1.Parameters.AddWithValue("id", reqWithEnrollmentId);
                await using var rdr1 = await check1.ExecuteReaderAsync();
                Assert.True(await rdr1.ReadAsync());
                Assert.False(rdr1.IsDBNull(0), "enrollment_id не повинен бути NULL");
                Assert.False(rdr1.IsDBNull(1), "requested_enrollment_id мав бути заповнений міграцією");
                Assert.Equal(heldEnrollmentId, rdr1.GetGuid(0));
                Assert.Equal(heldEnrollmentId, rdr1.GetGuid(1));
            }

            // 9. Перевіряємо запис з NULL:
            // - міграція коректно залишає NULL в обох полях
            await using (var check2 = new NpgsqlCommand(@"
                SELECT enrollment_id, requested_enrollment_id
                FROM enrollment_requests
                WHERE id = @id;", conn))
            {
                check2.Parameters.AddWithValue("id", reqWithNullEnrollmentId);
                await using var rdr2 = await check2.ExecuteReaderAsync();
                Assert.True(await rdr2.ReadAsync());
                Assert.True(rdr2.IsDBNull(0), "enrollment_id мав залишитися NULL");
                Assert.True(rdr2.IsDBNull(1), "requested_enrollment_id мав залишитися NULL");
            }

            // 10. Перевіряємо ідемпотентність: повторне виконання міграції 009 не викликає помилок і зберігає дані
            await using (var cmdReplay = new NpgsqlCommand(migrationSql, conn))
            {
                await cmdReplay.ExecuteNonQueryAsync();
            }

            await using (var check1Replay = new NpgsqlCommand(@"
                SELECT enrollment_id, requested_enrollment_id
                FROM enrollment_requests
                WHERE id = @id;", conn))
            {
                check1Replay.Parameters.AddWithValue("id", reqWithEnrollmentId);
                await using var rdrReplay = await check1Replay.ExecuteReaderAsync();
                Assert.True(await rdrReplay.ReadAsync());
                Assert.Equal(heldEnrollmentId, rdrReplay.GetGuid(0));
                Assert.Equal(heldEnrollmentId, rdrReplay.GetGuid(1));
            }

            // Скидаємо search_path перед штатним закриттям
            await using var resetCmd = new NpgsqlCommand("RESET search_path;", conn);
            await resetCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            testException = ex;
            throw;
        }
        finally
        {
            Exception? cleanupException = null;

            // Гарантоване видалення тимчасової схеми на окремому непопульованому з'єднанні
            try
            {
                var cleanupBuilder = new NpgsqlConnectionStringBuilder(TestEnvironment.ConnectionString)
                {
                    Pooling = false
                };
                await using var connCleanup = new NpgsqlConnection(cleanupBuilder.ConnectionString);
                await connCleanup.OpenAsync();
                await using var dropCmd = new NpgsqlCommand($@"
                    RESET search_path;
                    DROP SCHEMA IF EXISTS ""{tempSchema}"" CASCADE;", connCleanup);
                await dropCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                cleanupException = CombineExceptions(cleanupException, ex);
            }

            // Окреме очищення першого тестового запису
            try
            {
                await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, reqWithEnrollmentId);
            }
            catch (Exception ex)
            {
                cleanupException = CombineExceptions(cleanupException, ex);
            }

            // Окреме очищення другого тестового запису (виконується навіть при збої попереднього очищення)
            try
            {
                await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, reqWithNullEnrollmentId);
            }
            catch (Exception ex)
            {
                cleanupException = CombineExceptions(cleanupException, ex);
            }

            // Якщо очищення завершилося з помилкою, не маскуємо первинне виключення тесту
            if (cleanupException != null)
            {
                if (testException != null)
                {
                    throw new AggregateException(
                        $"Помилка під час виконання тесту міграції та очищення тимчасової схеми '{tempSchema}'.",
                        testException, cleanupException);
                }
                throw new InvalidOperationException(
                    $"Очищення тимчасової схеми міграції '{tempSchema}' завершилося з помилкою.", cleanupException);
            }
        }
    }

    [SkippableFact]
    public async Task Migration010_Schema_DecanateRosterUniqueConstraints()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        await using var conn = await TestEnvironment.OpenConnectionAsync();

        await using var cmd = new NpgsqlCommand(@"
            SELECT indexname
            FROM pg_indexes
            WHERE tablename = 'decanate_roster'
              AND indexname IN ('uq_decanate_roster_student_discipline', 'uq_decanate_roster_enrollment_id');", conn);

        var foundIndexes = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            foundIndexes.Add(reader.GetString(0));
        }

        Assert.Contains("uq_decanate_roster_student_discipline", foundIndexes);
        Assert.Contains("uq_decanate_roster_enrollment_id", foundIndexes);
    }
}
