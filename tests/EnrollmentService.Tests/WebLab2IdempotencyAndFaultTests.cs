using System.Net;
using System.Text;
using System.Text.Json;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class E2EHttpEndpointTests
{
    [SkippableFact]
    public async Task Web_O21_ConcurrentEnrollments_25Seats_40Students_AcrossTwoInstances_PreservesAllInvariants()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string groupId = $"GRP-E2E-O21-25-{Guid.NewGuid():N}"[..28];
        string discId = $"DISC-E2E-O21-25-{Guid.NewGuid():N}"[..28];
        const int seats = 25;
        const int totalStudents = 40;
        var studentIds = Enumerable.Range(1, totalStudents)
            .Select(i => $"student-e2e-o21-{i:00}-{Guid.NewGuid():N}"[..30])
            .ToArray();

        using var factoryA = _factory.WithWebHostBuilder(b => b.UseSetting("INSTANCE_ID", "app-a"));
        using var factoryB = _factory.WithWebHostBuilder(b => b.UseSetting("INSTANCE_ID", "app-b"));
        using var clientA = factoryA.CreateClient();
        using var clientB = factoryB.CreateClient();

        try
        {
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats,
                seats,
                DateTimeOffset.UtcNow.AddHours(-2),
                DateTimeOffset.UtcNow.AddHours(2),
                ects: 3.0m,
                dpcSandboxEnabled: false);

            foreach (var sid in studentIds)
            {
                await EnsureTestStudentAsync(sid, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            }

            using var startGate = new ManualResetEventSlim(false);
            var tasks = studentIds.Select((sid, idx) => Task.Run(async () =>
            {
                startGate.Wait();
                var targetClient = (idx % 2 == 0) ? clientA : clientB;
                using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
                req.Headers.Add("X-User-Id", sid);
                req.Headers.Add("Idempotency-Key", $"idem-o21-{sid}");
                req.Content = new StringContent(JsonSerializer.Serialize(new { group_id = groupId }), Encoding.UTF8, "application/json");
                return await targetClient.SendAsync(req);
            })).ToArray();

            startGate.Set();
            var responses = await Task.WhenAll(tasks);

            int createdCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
            int conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

            Assert.Equal(seats, createdCount);
            Assert.Equal(totalStudents - seats, conflictCount);

            await using var conn = await TestEnvironment.OpenConnectionAsync();

            await using (var cmdGroup = new NpgsqlCommand("SELECT seats, available FROM discipline_group WHERE id = @gid;", conn))
            {
                cmdGroup.Parameters.AddWithValue("gid", groupId);
                await using var reader = await cmdGroup.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(seats, reader.GetInt32(0));
                Assert.Equal(0, reader.GetInt32(1));
            }

            await using (var cmdEnr = new NpgsqlCommand("SELECT COUNT(*)::INT FROM enrollment WHERE group_id = @gid AND status IN ('held', 'confirmed');", conn))
            {
                cmdEnr.Parameters.AddWithValue("gid", groupId);
                int actualEnrolled = (int)(await cmdEnr.ExecuteScalarAsync())!;
                Assert.Equal(seats, actualEnrolled);
            }

            foreach (var resp in responses)
            {
                resp.Dispose();
            }
        }
        finally
        {
            foreach (var sid in studentIds)
            {
                await TestEnvironment.CleanupTestDataAsync(sid, null, null);
            }
            await TestEnvironment.CleanupTestDataAsync(null, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Web_O22_IdempotencyKey_ReturnsSavedOriginalResponse_EvenAfterConfirmationOrOnAnotherInstance()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = $"student-e2e-o22-orig-{Guid.NewGuid():N}"[..28];
        string groupId = $"GRP-E2E-O22-ORIG-{Guid.NewGuid():N}"[..28];
        string discId = $"DISC-E2E-O22-ORIG-{Guid.NewGuid():N}"[..28];
        string idemKey = $"idem-o22-orig-{Guid.NewGuid():N}";

        using var factoryA = _factory.WithWebHostBuilder(b => b.UseSetting("INSTANCE_ID", "app-a"));
        using var factoryB = _factory.WithWebHostBuilder(b => b.UseSetting("INSTANCE_ID", "app-b"));
        using var clientA = factoryA.CreateClient();
        using var clientB = factoryB.CreateClient();

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, 5, 0, 0.0m);
            await EnsureTestGroupAsync(groupId, discId, 10, 10, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(2), 4.0m);

            // 1. Перший запит на примірник app-a
            using var req1 = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
            req1.Headers.Add("X-User-Id", studentId);
            req1.Headers.Add("Idempotency-Key", idemKey);
            req1.Content = new StringContent($"{{  \"group_id\" : \"{groupId}\"  }}", Encoding.UTF8, "application/json");

            using var resp1 = await clientA.SendAsync(req1);
            Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
            using var doc1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync());
            string enrollmentId = doc1.RootElement.GetProperty("id").GetString()!;
            Assert.Equal("held", doc1.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, doc1.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("app-a", doc1.RootElement.GetProperty("instance_id").GetString());

            // 2. Підтверджуємо запис (status стає "confirmed", version стає 2)
            using var confirmReq = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId}/confirm");
            confirmReq.Headers.Add("X-User-Id", studentId);
            using var confirmResp = await clientA.SendAsync(confirmReq);
            Assert.Equal(HttpStatusCode.OK, confirmResp.StatusCode);

            // 3. Повторне надсилання POST /v1/enrollments із тим самим Idempotency-Key на ІНШИЙ примірник (app-b)
            // має повернути збережену ПОЧАТКОВУ відповідь (201 Created, status="held", version=1, instance_id="app-a")!
            using var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
            req2.Headers.Add("X-User-Id", studentId);
            req2.Headers.Add("Idempotency-Key", idemKey);
            req2.Content = new StringContent($"{{\"group_id\":\"{groupId}\"}}", Encoding.UTF8, "application/json");

            using var resp2 = await clientB.SendAsync(req2);
            Assert.Equal(HttpStatusCode.Created, resp2.StatusCode);
            Assert.Equal($"/v1/enrollments/{enrollmentId}", resp2.Headers.Location?.ToString());

            using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync());
            Assert.Equal(enrollmentId, doc2.RootElement.GetProperty("id").GetString());
            Assert.Equal("held", doc2.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, doc2.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("app-a", doc2.RootElement.GetProperty("instance_id").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Web_O22_IdempotencyKey_InProgressState_Returns409Conflict_Then201WhenCompleted()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = $"student-e2e-o22-proc-{Guid.NewGuid():N}"[..28];
        string groupId = $"GRP-E2E-O22-PROC-{Guid.NewGuid():N}"[..28];
        string discId = $"DISC-E2E-O22-PROC-{Guid.NewGuid():N}"[..28];
        string idemKey = $"idem-o22-proc-{Guid.NewGuid():N}";
        Guid enrollmentId = Guid.NewGuid();

        using var client = _factory.CreateClient();
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);

        try
        {
            await EnsureTestStudentAsync(studentId, 5, 0, 0.0m);
            await EnsureTestGroupAsync(groupId, discId, 10, 10, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(2), 3.0m);

            string fingerprint = EnrollmentDomainRules.ComputeWebEnrollmentFingerprint(studentId, groupId);

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using (var insCmd = new NpgsqlCommand(@"
                INSERT INTO enrollment (
                    id, student_id, group_id, discipline_id, status, version, created_at, expires_at,
                    idempotency_key, request_payload_hash, idempotency_operation, idempotency_state,
                    idempotency_response_status, idempotency_expires_at
                ) VALUES (
                    @id, @sid, @gid, @did, 'held', 1, NOW(), NOW() + INTERVAL '15 minutes',
                    @ik, @hash, 'POST /v1/enrollments', 'processing', 201, NOW() + INTERVAL '24 hours'
                );", conn))
            {
                insCmd.Parameters.AddWithValue("id", enrollmentId);
                insCmd.Parameters.AddWithValue("sid", studentId);
                insCmd.Parameters.AddWithValue("gid", groupId);
                insCmd.Parameters.AddWithValue("did", discId);
                insCmd.Parameters.AddWithValue("ik", idemKey);
                insCmd.Parameters.AddWithValue("hash", fingerprint);
                await insCmd.ExecuteNonQueryAsync();
            }

            // Поки ключ перебуває у стані 'processing', конкуруючий запит отримує 409 Conflict (idempotency_key_in_progress)
            using (var reqInFlight = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments"))
            {
                reqInFlight.Headers.Add("X-User-Id", studentId);
                reqInFlight.Headers.Add("Idempotency-Key", idemKey);
                reqInFlight.Content = new StringContent(JsonSerializer.Serialize(new { group_id = groupId }), Encoding.UTF8, "application/json");

                using var respInFlight = await client.SendAsync(reqInFlight);
                Assert.Equal(HttpStatusCode.Conflict, respInFlight.StatusCode);
                using var errDoc = JsonDocument.Parse(await respInFlight.Content.ReadAsStringAsync());
                Assert.Equal("idempotency_key_in_progress", errDoc.RootElement.GetProperty("error_code").GetString());
            }

            // Переводимо стан у 'completed'
            await using (var updCmd = new NpgsqlCommand(@"
                UPDATE enrollment
                SET idempotency_state = 'completed'
                WHERE id = @id;", conn))
            {
                updCmd.Parameters.AddWithValue("id", enrollmentId);
                await updCmd.ExecuteNonQueryAsync();
            }

            // Після завершення першої операції повтор повертає 201 Created
            using (var reqCompleted = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments"))
            {
                reqCompleted.Headers.Add("X-User-Id", studentId);
                reqCompleted.Headers.Add("Idempotency-Key", idemKey);
                reqCompleted.Content = new StringContent(JsonSerializer.Serialize(new { group_id = groupId }), Encoding.UTF8, "application/json");

                using var respCompleted = await client.SendAsync(reqCompleted);
                Assert.Equal(HttpStatusCode.Created, respCompleted.StatusCode);
                using var okDoc = JsonDocument.Parse(await respCompleted.Content.ReadAsStringAsync());
                Assert.Equal(enrollmentId.ToString("D"), okDoc.RootElement.GetProperty("id").GetString());
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Web_O22_IdempotencyKey_BatchCleanup_ClearsExpiredKeysWithoutDeletingEnrollments()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = $"student-e2e-o22-ttl-{Guid.NewGuid():N}"[..28];
        string groupId = $"GRP-E2E-O22-TTL-{Guid.NewGuid():N}"[..28];
        string discId = $"DISC-E2E-O22-TTL-{Guid.NewGuid():N}"[..28];
        string expiredKey = $"idem-o22-expired-{Guid.NewGuid():N}";
        Guid enrollmentId = Guid.NewGuid();

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, 5, 1, 3.0m);
            await EnsureTestGroupAsync(groupId, discId, 10, 9, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(2), 3.0m);

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using (var insCmd = new NpgsqlCommand(@"
                INSERT INTO enrollment (
                    id, student_id, group_id, discipline_id, status, version, created_at, expires_at,
                    idempotency_key, request_payload_hash, idempotency_operation, idempotency_state,
                    idempotency_response_status, idempotency_expires_at
                ) VALUES (
                    @id, @sid, @gid, @did, 'confirmed', 2, NOW() - INTERVAL '2 days', NOW() - INTERVAL '1 day',
                    @ik, 'sha256:expired', 'POST /v1/enrollments', 'completed', 201, NOW() - INTERVAL '1 hour'
                );", conn))
            {
                insCmd.Parameters.AddWithValue("id", enrollmentId);
                insCmd.Parameters.AddWithValue("sid", studentId);
                insCmd.Parameters.AddWithValue("gid", groupId);
                insCmd.Parameters.AddWithValue("did", discId);
                insCmd.Parameters.AddWithValue("ik", expiredKey);
                await insCmd.ExecuteNonQueryAsync();
            }

            var enrollmentSvc = _factory.Services.GetRequiredService<EnrollmentService.Services.EnrollmentService>();
            int cleaned = await enrollmentSvc.CleanupExpiredIdempotencyKeysAsync(batchSize: 50);
            Assert.True(cleaned >= 1);

            await using (var verifyCmd = new NpgsqlCommand(@"
                SELECT status, idempotency_key, idempotency_expires_at
                FROM enrollment
                WHERE id = @id;", conn))
            {
                verifyCmd.Parameters.AddWithValue("id", enrollmentId);
                await using var reader = await verifyCmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync(), "Предметний запис enrollment не повинен видалятися при очищенні простроченого ключа ідемпотентності");
                Assert.Equal("confirmed", reader.GetString(0));
                Assert.True(reader.IsDBNull(1), "Прострочений idempotency_key мав бути очищений");
                Assert.True(reader.IsDBNull(2), "idempotency_expires_at мав бути очищений");
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Web_O23_FaultAfterCommitBeforeResponse_TriggersOnce_CommitsData_AndRetryOnSecondInstanceReturns201()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = $"student-e2e-o23-flt-{Guid.NewGuid():N}"[..28];
        string groupId = $"GRP-E2E-O23-FLT-{Guid.NewGuid():N}"[..28];
        string discId = $"DISC-E2E-O23-FLT-{Guid.NewGuid():N}"[..28];
        string idemKey = $"idem-o23-fault-{Guid.NewGuid():N}";
        string tempFaultsDir = Path.Combine(Path.GetTempPath(), $"c10_faults_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFaultsDir);
        string flagFile = Path.Combine(tempFaultsDir, FaultInjector.AfterCommitBeforeResponseFaultName);

        using var factoryA = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("INSTANCE_ID", "app-a");
            b.UseSetting("FAULTS", "on");
            b.UseSetting("FAULTS_DIR", tempFaultsDir);
        });
        using var factoryB = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("INSTANCE_ID", "app-b");
            b.UseSetting("FAULTS", "on");
            b.UseSetting("FAULTS_DIR", tempFaultsDir);
        });

        using var clientA = factoryA.CreateClient();
        using var clientB = factoryB.CreateClient();

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, 5, 0, 0.0m);
            await EnsureTestGroupAsync(groupId, discId, 10, 10, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(2), 5.0m);

            // Активуємо одноразовий прапорець /faults/after_commit_before_response
            await File.WriteAllTextAsync(flagFile, "1");

            int faultTriggeredCount = 0;
            Guid faultedEnrollmentId = Guid.Empty;
            FaultInjector.InProcessTestFaultIntercept = (inst, enrId, sid, ik) =>
            {
                Interlocked.Increment(ref faultTriggeredCount);
                faultedEnrollmentId = enrId;
                throw new IOException($"Simulated process crash after COMMIT on {inst} for enrollment {enrId}");
            };

            try
            {
                using var req1 = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
                req1.Headers.Add("X-User-Id", studentId);
                req1.Headers.Add("Idempotency-Key", idemKey);
                req1.Content = new StringContent(JsonSerializer.Serialize(new { group_id = groupId }), Encoding.UTF8, "application/json");

                // Перший запит обривається після COMMIT у PostgreSQL (клієнт не отримує 201 Created)
                bool lostResponse = false;
                try
                {
                    using var resp1 = await clientA.SendAsync(req1);
                    if ((int)resp1.StatusCode >= 500)
                    {
                        lostResponse = true;
                    }
                }
                catch
                {
                    lostResponse = true;
                }

                Assert.True(lostResponse, "Перший запит мав втратити відповідь після COMMIT через прапорець after_commit_before_response");
                Assert.Equal(1, faultTriggeredCount);
                Assert.False(File.Exists(flagFile), "Прапорець /faults/after_commit_before_response мав бути атомарно видалений після першого спрацювання");
            }
            finally
            {
                FaultInjector.InProcessTestFaultIntercept = null;
            }

            // Клієнт повторює запит із ТИМ САМИМ Idempotency-Key на примірник app-b:
            // отримує збережений 201 Created без повторного списання місця!
            using var reqRetry = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
            reqRetry.Headers.Add("X-User-Id", studentId);
            reqRetry.Headers.Add("Idempotency-Key", idemKey);
            reqRetry.Content = new StringContent(JsonSerializer.Serialize(new { group_id = groupId }), Encoding.UTF8, "application/json");

            using var respRetry = await clientB.SendAsync(reqRetry);
            Assert.Equal(HttpStatusCode.Created, respRetry.StatusCode);

            using var retryDoc = JsonDocument.Parse(await respRetry.Content.ReadAsStringAsync());
            Assert.Equal(faultedEnrollmentId.ToString("D"), retryDoc.RootElement.GetProperty("id").GetString());
            Assert.Equal("held", retryDoc.RootElement.GetProperty("status").GetString());
            Assert.Equal("app-a", retryDoc.RootElement.GetProperty("instance_id").GetString());

            // Перевіряємо в PostgreSQL: рівно 1 запис у enrollment і available зменшився лише на 1 (10 -> 9)
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using (var cmdCnt = new NpgsqlCommand("SELECT COUNT(*)::INT FROM enrollment WHERE student_id = @sid AND group_id = @gid;", conn))
            {
                cmdCnt.Parameters.AddWithValue("sid", studentId);
                cmdCnt.Parameters.AddWithValue("gid", groupId);
                Assert.Equal(1, (int)(await cmdCnt.ExecuteScalarAsync())!);
            }

            await using (var cmdAvail = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @gid;", conn))
            {
                cmdAvail.Parameters.AddWithValue("gid", groupId);
                Assert.Equal(9, (int)(await cmdAvail.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            FaultInjector.InProcessTestFaultIntercept = null;
            try
            {
                if (Directory.Exists(tempFaultsDir))
                {
                    Directory.Delete(tempFaultsDir, recursive: true);
                }
            }
            catch { }
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }
}
