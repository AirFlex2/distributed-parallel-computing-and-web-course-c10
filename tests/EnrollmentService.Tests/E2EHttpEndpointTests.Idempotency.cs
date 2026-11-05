using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnrollmentService;
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
    public async Task DpcQueue_IdempotentReplay_WhenQueueIsFull_Returns202WithSameId()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-idem-full";
        string groupId = "GRP-E2E-IDEM-G1";
        string discId = "DISC-E2E-IDEM-1";
        string idempotencyKey = $"idem-full-queue-{Guid.NewGuid():N}";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 10, available: 10, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            // 1. Надсилаємо початковий запит із ключем ідемпотентності
            var postContent = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                Encoding.UTF8,
                "application/json");
            postContent.Headers.Add("Idempotency-Key", idempotencyKey);

            var resp1 = await client.PostAsync("/v1/enrollment-requests", postContent);
            Assert.Equal(HttpStatusCode.Accepted, resp1.StatusCode);
            var json1 = await resp1.Content.ReadAsStringAsync();
            using var doc1 = JsonDocument.Parse(json1);
            string id1 = doc1.RootElement.GetProperty("id").GetString()!;
            createdRequestId = Guid.Parse(id1);

            // 2. Штучно заповнюємо чергу слотів до ліміту
            var queue = _factory.Services.GetRequiredService<IBoundedRequestQueue>();
            int reservedSlots = 0;
            while (queue.TryReserveSlot())
            {
                reservedSlots++;
            }

            try
            {
                // Переконуємося, що черга дійсно заповнена: новий невідомий запит отримує 429 з Retry-After: 1
                var newContent = new StringContent(
                    JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                    Encoding.UTF8,
                    "application/json");
                newContent.Headers.Add("Idempotency-Key", $"new-key-{Guid.NewGuid():N}");
                var resp429 = await client.PostAsync("/v1/enrollment-requests", newContent);
                Assert.Equal(HttpStatusCode.TooManyRequests, resp429.StatusCode);
                Assert.True(resp429.Headers.Contains("Retry-After"));

                // 3. ПОВТОР запиту з тим самим Idempotency-Key при заповненій черзі повертає 202 Accepted з тим самим ID!
                var retryContent = new StringContent(
                    JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                    Encoding.UTF8,
                    "application/json");
                retryContent.Headers.Add("Idempotency-Key", idempotencyKey);

                var retryResp = await client.PostAsync("/v1/enrollment-requests", retryContent);
                Assert.Equal(HttpStatusCode.Accepted, retryResp.StatusCode);
                var retryJson = await retryResp.Content.ReadAsStringAsync();
                using var retryDoc = JsonDocument.Parse(retryJson);
                string retryId = retryDoc.RootElement.GetProperty("id").GetString()!;
                Assert.Equal(id1, retryId);
            }
            finally
            {
                // Звільняємо штучно зарезервовані слоти, щоб воркер міг продовжити роботу
                for (int i = 0; i < reservedSlots; i++)
                {
                    queue.ReleaseSlot();
                }
            }

            // Дочікуємося завершення обробки заявки воркером
            await WaitForTerminalStatusAsync(client, createdRequestId.Value);

            // Перевіряємо предметний ефект: рівно один запис у відомості, списано рівно одне місце
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*)::INT FROM decanate_roster WHERE request_id = @reqId;", conn);
            cmdRoster.Parameters.AddWithValue("reqId", createdRequestId.Value);
            int rosterCount = (int)(await cmdRoster.ExecuteScalarAsync())!;
            Assert.Equal(1, rosterCount);

            await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
            cmdGrp.Parameters.AddWithValue("grpId", groupId);
            int availableSeats = (int)(await cmdGrp.ExecuteScalarAsync())!;
            Assert.Equal(9, availableSeats);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task DpcQueue_IdempotentReplay_WithDifferentStudentId_Returns409Conflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId1 = "student-e2e-diff-s1";
        string studentId2 = "student-e2e-diff-s2";
        string groupId = "GRP-E2E-DIFF-G1";
        string discId = "DISC-E2E-DIFF-1";
        string idempotencyKey = $"idem-diff-student-{Guid.NewGuid():N}";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId1, groupId, discId);
        await TestEnvironment.CleanupTestDataAsync(studentId2, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId1, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestStudentAsync(studentId2, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 10, available: 10, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            // 1. Перший запит зі studentId1
            var postContent = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId1, group_id = groupId }),
                Encoding.UTF8,
                "application/json");
            postContent.Headers.Add("Idempotency-Key", idempotencyKey);

            var resp1 = await client.PostAsync("/v1/enrollment-requests", postContent);
            Assert.Equal(HttpStatusCode.Accepted, resp1.StatusCode);
            var json1 = await resp1.Content.ReadAsStringAsync();
            using var doc1 = JsonDocument.Parse(json1);
            createdRequestId = Guid.Parse(doc1.RootElement.GetProperty("id").GetString()!);

            // 2. Повторний запит із тим же ключем, але іншим studentId2 -> 409 Conflict (idempotency_conflict)
            var conflictContent = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId2, group_id = groupId }),
                Encoding.UTF8,
                "application/json");
            conflictContent.Headers.Add("Idempotency-Key", idempotencyKey);

            var conflictResp = await client.PostAsync("/v1/enrollment-requests", conflictContent);
            Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);
            var conflictJson = await conflictResp.Content.ReadAsStringAsync();
            using var conflictDoc = JsonDocument.Parse(conflictJson);
            Assert.Equal(409, conflictDoc.RootElement.GetProperty("status").GetInt32());
            Assert.Contains("idempotency_conflict", conflictDoc.RootElement.GetProperty("type").GetString()!);

            // 3. Дочікуємося завершення першої заявки
            await WaitForTerminalStatusAsync(client, createdRequestId.Value);

            // 4. Перевіряємо предметний ефект: тільки studentId1 у відомості, списано 1 місце
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdRoster = new NpgsqlCommand("SELECT student_id FROM decanate_roster WHERE request_id = @reqId;", conn);
            cmdRoster.Parameters.AddWithValue("reqId", createdRequestId.Value);
            string? enrolledStudent = (string?)(await cmdRoster.ExecuteScalarAsync());
            Assert.Equal(studentId1, enrolledStudent);

            await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
            cmdGrp.Parameters.AddWithValue("grpId", groupId);
            int availableSeats = (int)(await cmdGrp.ExecuteScalarAsync())!;
            Assert.Equal(9, availableSeats);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId1, groupId, discId, createdRequestId);
            await TestEnvironment.CleanupTestDataAsync(studentId2, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task DpcQueue_IdempotentReplay_WithDifferentGroupId_Returns409Conflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-diff-grp";
        string groupId1 = "GRP-E2E-DGRP-G1";
        string groupId2 = "GRP-E2E-DGRP-G2";
        string discId = "DISC-E2E-DGRP-1";
        string idempotencyKey = $"idem-diff-group-{Guid.NewGuid():N}";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId1, discId);
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId1, discId, seats: 10, available: 10, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1));
            await EnsureTestGroupAsync(groupId2, discId, seats: 10, available: 10, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            // 1. Перший запит із groupId1
            var postContent = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId1 }),
                Encoding.UTF8,
                "application/json");
            postContent.Headers.Add("Idempotency-Key", idempotencyKey);

            var resp1 = await client.PostAsync("/v1/enrollment-requests", postContent);
            Assert.Equal(HttpStatusCode.Accepted, resp1.StatusCode);
            var json1 = await resp1.Content.ReadAsStringAsync();
            using var doc1 = JsonDocument.Parse(json1);
            createdRequestId = Guid.Parse(doc1.RootElement.GetProperty("id").GetString()!);

            // 2. Повторний запит із тим же ключем, але іншим groupId2 -> 409 Conflict (idempotency_conflict)
            var conflictContent = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId2 }),
                Encoding.UTF8,
                "application/json");
            conflictContent.Headers.Add("Idempotency-Key", idempotencyKey);

            var conflictResp = await client.PostAsync("/v1/enrollment-requests", conflictContent);
            Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);
            var conflictJson = await conflictResp.Content.ReadAsStringAsync();
            using var conflictDoc = JsonDocument.Parse(conflictJson);
            Assert.Equal(409, conflictDoc.RootElement.GetProperty("status").GetInt32());
            Assert.Contains("idempotency_conflict", conflictDoc.RootElement.GetProperty("type").GetString()!);

            // 3. Дочікуємося завершення першої заявки
            await WaitForTerminalStatusAsync(client, createdRequestId.Value);

            // 4. Перевіряємо предметний ефект: тільки groupId1 у відомості
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdRoster = new NpgsqlCommand("SELECT group_id FROM decanate_roster WHERE request_id = @reqId;", conn);
            cmdRoster.Parameters.AddWithValue("reqId", createdRequestId.Value);
            string? enrolledGroup = (string?)(await cmdRoster.ExecuteScalarAsync());
            Assert.Equal(groupId1, enrolledGroup);

            await using var cmdGrp1 = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
            cmdGrp1.Parameters.AddWithValue("grpId", groupId1);
            int available1 = (int)(await cmdGrp1.ExecuteScalarAsync())!;
            Assert.Equal(9, available1);

            await using var cmdGrp2 = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
            cmdGrp2.Parameters.AddWithValue("grpId", groupId2);
            int available2 = (int)(await cmdGrp2.ExecuteScalarAsync())!;
            Assert.Equal(10, available2);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId1, discId, createdRequestId);
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId);
        }
    }

    [SkippableFact]
    public async Task DpcQueue_IdempotentReplay_GuaranteesSingleDatabaseRecord()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-single-rec";
        string groupId = "GRP-E2E-SREC-G1";
        string discId = "DISC-E2E-SREC-1";
        string idempotencyKey = $"idem-single-rec-{Guid.NewGuid():N}";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 10, available: 10, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            for (int i = 0; i < 3; i++)
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                    Encoding.UTF8,
                    "application/json");
                content.Headers.Add("Idempotency-Key", idempotencyKey);

                var resp = await client.PostAsync("/v1/enrollment-requests", content);
                Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                string reqIdStr = doc.RootElement.GetProperty("id").GetString()!;
                if (createdRequestId == null)
                {
                    createdRequestId = Guid.Parse(reqIdStr);
                }
                else
                {
                    Assert.Equal(createdRequestId.Value.ToString("D"), reqIdStr);
                }
            }

            // Дочікуємося завершення обробки заявки воркером
            await WaitForTerminalStatusAsync(client, createdRequestId!.Value);

            // Перевіряємо базу даних: рівно один запис для даного ключа ідемпотентності
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*)::INT FROM enrollment_requests WHERE idempotency_key = @key;", conn);
            cmd.Parameters.AddWithValue("key", idempotencyKey);
            int count = (int)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(1, count);

            // Перевіряємо предметний ефект: рівно один запис у відомості та одне списання місця
            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*)::INT FROM decanate_roster WHERE request_id = @reqId;", conn);
            cmdRoster.Parameters.AddWithValue("reqId", createdRequestId.Value);
            int rosterCount = (int)(await cmdRoster.ExecuteScalarAsync())!;
            Assert.Equal(1, rosterCount);

            await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
            cmdGrp.Parameters.AddWithValue("grpId", groupId);
            int availableSeats = (int)(await cmdGrp.ExecuteScalarAsync())!;
            Assert.Equal(9, availableSeats);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task DpcQueue_ConcurrentRequestsWithSameKey_ProduceSingleRecordAndConsistentResponse()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-conc-idem";
        string groupId = "GRP-E2E-CIDEM-G1";
        string discId = "DISC-E2E-CIDEM-1";
        string idempotencyKey = $"idem-concurrent-{Guid.NewGuid():N}";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 10, available: 10, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var tasks = Enumerable.Range(0, 5).Select(async _ =>
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                    Encoding.UTF8,
                    "application/json");
                content.Headers.Add("Idempotency-Key", idempotencyKey);
                return await client.PostAsync("/v1/enrollment-requests", content);
            }).ToList();

            var responses = await Task.WhenAll(tasks);
            var responseIds = new List<string>();

            foreach (var resp in responses)
            {
                Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                responseIds.Add(doc.RootElement.GetProperty("id").GetString()!);
            }

            // Усі 5 конкурентних запитів повинні повернути ідентичний ID заявки
            string firstId = responseIds[0];
            createdRequestId = Guid.Parse(firstId);
            Assert.All(responseIds, id => Assert.Equal(firstId, id));

            // Дочікуємося завершення обробки заявки воркером
            await WaitForTerminalStatusAsync(client, createdRequestId.Value);

            // У базі повинен бути строго один запис enrollment_requests
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*)::INT FROM enrollment_requests WHERE idempotency_key = @key;", conn);
            cmd.Parameters.AddWithValue("key", idempotencyKey);
            int count = (int)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(1, count);

            // Перевіряємо предметний ефект: рівно один запис у decanate_roster та рівно одне списане місце
            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*)::INT FROM decanate_roster WHERE request_id = @reqId;", conn);
            cmdRoster.Parameters.AddWithValue("reqId", createdRequestId.Value);
            int rosterCount = (int)(await cmdRoster.ExecuteScalarAsync())!;
            Assert.Equal(1, rosterCount);

            await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
            cmdGrp.Parameters.AddWithValue("grpId", groupId);
            int availableSeats = (int)(await cmdGrp.ExecuteScalarAsync())!;
            Assert.Equal(9, availableSeats);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task Web_O22_IdempotencyKey_SamePayload_ReturnsSavedResultAndDoesNotDeductSeats()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-idem-1";
        string groupId = "GRP-E2E-IDEM-G1";
        string discId = "DISC-E2E-IDEM-1";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 5,
                available: 5,
                opensAt: DateTimeOffset.UtcNow.AddDays(-1),
                closesAt: DateTimeOffset.UtcNow.AddDays(1),
                ects: 5.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-web-01");

            var content1 = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response1 = await client.PostAsync("/v1/enrollments", content1);

            Assert.Equal(HttpStatusCode.Created, response1.StatusCode);
            var loc1 = response1.Headers.Location?.ToString();
            Assert.NotNull(loc1);
            var json1 = await response1.Content.ReadAsStringAsync();
            using var doc1 = JsonDocument.Parse(json1);
            var root1 = doc1.RootElement;
            string id1 = root1.GetProperty("id").GetString()!;

            // Повторний запит із тим самим Idempotency-Key та тим самим тілом
            var content2 = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response2 = await client.PostAsync("/v1/enrollments", content2);

            Assert.Equal(HttpStatusCode.Created, response2.StatusCode);
            var loc2 = response2.Headers.Location?.ToString();
            Assert.Equal(loc1, loc2);
            var json2 = await response2.Content.ReadAsStringAsync();
            using var doc2 = JsonDocument.Parse(json2);
            var root2 = doc2.RootElement;
            string id2 = root2.GetProperty("id").GetString()!;
            Assert.Equal(id1, id2);

            // Перевіряємо стан БД: списано рівно одне місце (available = 4, записів = 1)
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @id;", conn);
            cmdGrp.Parameters.AddWithValue("id", groupId);
            int available = (int)(await cmdGrp.ExecuteScalarAsync())!;
            Assert.Equal(4, available);

            await using var cmdCnt = new NpgsqlCommand("SELECT COUNT(*) FROM enrollment WHERE student_id = @sid;", conn);
            cmdCnt.Parameters.AddWithValue("sid", studentId);
            long cnt = (long)(await cmdCnt.ExecuteScalarAsync())!;
            Assert.Equal(1L, cnt);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Web_O22_IdempotencyKey_DifferentPayload_Returns422UnprocessableContent()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-idem-2";
        string groupId1 = "GRP-E2E-IDEM-G2A";
        string groupId2 = "GRP-E2E-IDEM-G2B";
        string discId1 = "DISC-E2E-IDEM-2A";
        string discId2 = "DISC-E2E-IDEM-2B";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId1, discId1);
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId2);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId1,
                discId1,
                seats: 5,
                available: 5,
                opensAt: DateTimeOffset.UtcNow.AddDays(-1),
                closesAt: DateTimeOffset.UtcNow.AddDays(1),
                ects: 4.0m);

            await EnsureTestGroupAsync(
                groupId2,
                discId2,
                seats: 5,
                available: 5,
                opensAt: DateTimeOffset.UtcNow.AddDays(-1),
                closesAt: DateTimeOffset.UtcNow.AddDays(1),
                ects: 4.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", "test-conflict-idem-key");

            // Перший запит
            var content1 = new StringContent($"{{\"group_id\": \"{groupId1}\"}}", Encoding.UTF8, "application/json");
            var response1 = await client.PostAsync("/v1/enrollments", content1);
            Assert.Equal(HttpStatusCode.Created, response1.StatusCode);

            // Другий запит із тим самим ключем, але іншою групою -> 422 Unprocessable Content
            var content2 = new StringContent($"{{\"group_id\": \"{groupId2}\"}}", Encoding.UTF8, "application/json");
            var response2 = await client.PostAsync("/v1/enrollments", content2);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response2.StatusCode);

            var json2 = await response2.Content.ReadAsStringAsync();
            using var doc2 = JsonDocument.Parse(json2);
            var root2 = doc2.RootElement;
            Assert.Equal(422, root2.GetProperty("status").GetInt32());
            Assert.Equal("idempotency_key_payload_mismatch", root2.GetProperty("error_code").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId1, discId1);
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId2);
        }
    }

    [SkippableFact]
    public async Task PostEnrollment_MissingIdempotencyKey_Returns400BadRequest()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");

        var content = new StringContent("{\"group_id\": \"DISC-WEB-01\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/enrollments", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Contains("missing_idempotency_key", root.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task PostEnrollment_EmptyOrWhitespaceIdempotencyKey_Returns400BadRequest()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "   ");

        var content = new StringContent("{\"group_id\": \"DISC-WEB-01\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/enrollments", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("missing_idempotency_key", doc.RootElement.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task PostEnrollment_TooLongIdempotencyKey_Returns400BadRequest()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");
        client.DefaultRequestHeaders.Add("Idempotency-Key", new string('x', 129));

        var content = new StringContent("{\"group_id\": \"DISC-WEB-01\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/enrollments", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("invalid_idempotency_key", doc.RootElement.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task Web_O22_PostEnrollment_ConcurrentReplay_CreatesOnlyOneRecord()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-idem-conc";
        string groupId = "GRP-E2E-IDEM-CONC";
        string discId = "DISC-E2E-IDEM-CONC";
        string idempotencyKey = $"test-idem-conc-{Guid.NewGuid():N}";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 5,
                available: 5,
                opensAt: DateTimeOffset.UtcNow.AddDays(-1),
                closesAt: DateTimeOffset.UtcNow.AddDays(1),
                ects: 4.0m);

            var client1 = _factory.CreateClient();
            client1.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client1.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

            var client2 = _factory.CreateClient();
            client2.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client2.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

            var task1 = client1.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            var task2 = client2.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));

            var responses = await Task.WhenAll(task1, task2);

            Assert.Equal(HttpStatusCode.Created, responses[0].StatusCode);
            Assert.Equal(HttpStatusCode.Created, responses[1].StatusCode);

            var json1 = await responses[0].Content.ReadAsStringAsync();
            var json2 = await responses[1].Content.ReadAsStringAsync();
            using var doc1 = JsonDocument.Parse(json1);
            using var doc2 = JsonDocument.Parse(json2);

            string id1 = doc1.RootElement.GetProperty("id").GetString()!;
            string id2 = doc2.RootElement.GetProperty("id").GetString()!;
            Assert.Equal(id1, id2);

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @id;", conn);
            cmdGrp.Parameters.AddWithValue("id", groupId);
            int available = (int)(await cmdGrp.ExecuteScalarAsync())!;
            Assert.Equal(4, available);

            await using var cmdCnt = new NpgsqlCommand("SELECT COUNT(*) FROM enrollment WHERE student_id = @sid;", conn);
            cmdCnt.Parameters.AddWithValue("sid", studentId);
            long cnt = (long)(await cmdCnt.ExecuteScalarAsync())!;
            Assert.Equal(1L, cnt);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableTheory]
    [InlineData("queued")]
    [InlineData("processing")]
    [InlineData("done")]
    [InlineData("rejected")]
    [InlineData("failed")]
    public async Task PostEnrollmentRequests_IdempotentReplay_AcrossAllFiveStates_Returns202(string expectedStatus)
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        string studentId = "student-idemp-all-states";
        string groupId = "DISC-DPC-01";
        string discId = "DISC-DPC-01";
        Guid requestId = Guid.NewGuid();
        string ik = $"ik-all-states-{expectedStatus}-{Guid.NewGuid():N}";

        try
        {
            await EnsureTestStudentAsync(studentId);
            await EnsureTestGroupAsync(groupId, discId, seats: 10, available: 10, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO enrollment_requests (
                        id, student_id, group_id, status, created_at, idempotency_key, request_payload_hash
                    ) VALUES (
                        @id, @studentId, @groupId, @status, NOW(), @ik, @hash
                    );", conn);
                cmd.Parameters.AddWithValue("id", requestId);
                cmd.Parameters.AddWithValue("studentId", studentId);
                cmd.Parameters.AddWithValue("groupId", groupId);
                cmd.Parameters.AddWithValue("status", expectedStatus);
                cmd.Parameters.AddWithValue("ik", ik);
                cmd.Parameters.AddWithValue("hash", $"{studentId}:{groupId}");
                await cmd.ExecuteNonQueryAsync();
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollment-requests");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);
            req.Headers.Add("Idempotency-Key", ik);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                Encoding.UTF8,
                "application/json");

            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            Assert.Equal($"/v1/enrollment-requests/{requestId}", resp.Headers.Location?.ToString());

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(requestId.ToString(), root.GetProperty("id").GetString());
            string returnedStatus = root.GetProperty("status").GetString()!;
            if (expectedStatus == "queued")
            {
                Assert.True(returnedStatus is "queued" or "processing");
            }
            else
            {
                Assert.Equal(expectedStatus, returnedStatus);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, requestId);
        }
    }

    [SkippableFact]
    public async Task Web_O22_IdempotencyKey_ConcurrentDifferentPayload_OneSucceedsOneReturns422()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-idem-race";
        string groupId1 = "GRP-IDEM-RACE-1";
        string discId1 = "DISC-IDEM-RACE-1";
        string groupId2 = "GRP-IDEM-RACE-2";
        string discId2 = "DISC-IDEM-RACE-2";
        string idempotencyKey = $"race-ik-{Guid.NewGuid():N}";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId1, discId1);
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId2);

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId1, discId1, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);
            await EnsureTestGroupAsync(groupId2, discId2, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);

            var client1 = _factory.CreateClient();
            client1.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client1.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

            var client2 = _factory.CreateClient();
            client2.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client2.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

            var task1 = client1.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId1}\"}}", Encoding.UTF8, "application/json"));
            var task2 = client2.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId2}\"}}", Encoding.UTF8, "application/json"));

            var responses = await Task.WhenAll(task1, task2);
            var statusCodes = responses.Select(r => r.StatusCode).ToList();

            Assert.Contains(HttpStatusCode.Created, statusCodes);
            Assert.Contains(HttpStatusCode.UnprocessableEntity, statusCodes);

            var failedResp = responses.First(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
            var failedJson = await failedResp.Content.ReadAsStringAsync();
            using var failedDoc = JsonDocument.Parse(failedJson);
            Assert.Equal("idempotency_key_payload_mismatch", failedDoc.RootElement.GetProperty("error_code").GetString());

            // Перевіряємо, що в базі створено рівно один запис
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM enrollment WHERE student_id = @sid;", conn);
                cmd.Parameters.AddWithValue("sid", studentId);
                long count = (long)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal(1L, count);

                // Перевіряємо залишок місць: сума списаних місць у двох групах має дорівнювати рівно 1
                await using var cmdSeats1 = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @gid;", conn);
                cmdSeats1.Parameters.AddWithValue("gid", groupId1);
                int a1 = (int)(await cmdSeats1.ExecuteScalarAsync())!;

                await using var cmdSeats2 = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @gid;", conn);
                cmdSeats2.Parameters.AddWithValue("gid", groupId2);
                int a2 = (int)(await cmdSeats2.ExecuteScalarAsync())!;

                Assert.Equal(9, a1 + a2); // 5 + 5 - 1 = 9
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId1, discId1);
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId2);
        }
    }

    [SkippableFact]
    public async Task Registrations_Idempotency_RepeatBeforeProcessing_ReturnsSameAccepted()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "stud-reg-idem-early";
        string groupId = "GRP-REG-IDEM-EARLY";
        string discId = "DISC-REG-IDEM-EARLY";
        string ik = $"ik-early-{Guid.NewGuid():N}";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);

        var options = _factory.Services.GetRequiredService<ServiceOptions>();
        bool originalAllowOverrides = options.AllowTestOverrides;
        options.AllowTestOverrides = true;

        var processor = _factory.Services.GetRequiredService<EnrollmentRequestProcessor>();
        var requestRepo = _factory.Services.GetRequiredService<EnrollmentRequestRepository>();
        var hookEnteredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        processor.TestPreProcessingHookAsync = async item =>
        {
            if (item.StudentId == studentId)
            {
                hookEnteredTcs.TrySetResult(true);
                await pauseTcs.Task;
            }
        };

        Guid? reqId1 = null;
        using var client = _factory.CreateClient();

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-web-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            // Перший виклик з Idempotency-Key
            using var req1 = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId}/registrations");
            req1.Headers.Add("Idempotency-Key", ik);
            var resp1 = await client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.Accepted, resp1.StatusCode);
            var json1 = await resp1.Content.ReadAsStringAsync();
            using var doc1 = JsonDocument.Parse(json1);
            reqId1 = Guid.Parse(doc1.RootElement.GetProperty("id").GetString()!);
            string loc1 = resp1.Headers.Location?.ToString() ?? "";

            // Очікуємо, що воркер гарантовано підхопив заявку і зупинився в TestPreProcessingHookAsync:
            await hookEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(hookEnteredTcs.Task.IsCompletedSuccessfully, "Воркер мав обов'язково зайти в TestPreProcessingHookAsync і призупинити обробку");

            // Гарантуємо, що воркер ще не встиг завершити обробку заявки:
            var reqRecord = await requestRepo.GetStatusByIdAsync(reqId1.Value);
            Assert.NotNull(reqRecord);
            Assert.NotEqual("done", reqRecord.Status);

            // Повторний виклик з тим самим Idempotency-Key ДО завершення обробки
            using var req2 = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId}/registrations");
            req2.Headers.Add("Idempotency-Key", ik);
            var resp2 = await client.SendAsync(req2);
            Assert.Equal(HttpStatusCode.Accepted, resp2.StatusCode);
            var json2 = await resp2.Content.ReadAsStringAsync();
            using var doc2 = JsonDocument.Parse(json2);
            Guid reqId2 = Guid.Parse(doc2.RootElement.GetProperty("id").GetString()!);
            string loc2 = resp2.Headers.Location?.ToString() ?? "";

            Assert.Equal(reqId1.Value, reqId2);
            Assert.Equal(loc1, loc2);
        }
        finally
        {
            pauseTcs.TrySetResult(true);
            processor.TestPreProcessingHookAsync = null;
            options.AllowTestOverrides = originalAllowOverrides;

            // Якщо заявку було створено, очікуємо її переходу в термінальний стан (done, rejected або failed).
            // У разі таймауту виключення не приглушується: помилка фіксується явно, а CleanupTestDataAsync
            // НЕ викликається, щоб запобігти гонитві з воркером та зберегти дані для діагностики.
            if (reqId1.HasValue)
            {
                await WaitForTerminalStatusAsync(client, reqId1.Value);
            }

            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Registrations_Idempotency_RepeatAfterDone_ReturnsSameAccepted()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "stud-reg-idem-done";
        string groupId = "GRP-REG-IDEM-DONE";
        string discId = "DISC-REG-IDEM-DONE";
        string ik = $"ik-done-{Guid.NewGuid():N}";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-web-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            using var regReq = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId}/registrations");
            regReq.Headers.Add("Idempotency-Key", ik);
            var regResp = await client.SendAsync(regReq);
            Assert.Equal(HttpStatusCode.Accepted, regResp.StatusCode);
            var regJson = await regResp.Content.ReadAsStringAsync();
            using var regDoc = JsonDocument.Parse(regJson);
            Guid reqId = Guid.Parse(regDoc.RootElement.GetProperty("id").GetString()!);

            // Очікуємо переходу у done
            var dpcClient = _factory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            bool isDone = false;
            for (int i = 0; i < 40 && !isDone; i++)
            {
                await Task.Delay(100);
                var getResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{reqId}");
                if (getResp.StatusCode == HttpStatusCode.OK)
                {
                    var getJson = await getResp.Content.ReadAsStringAsync();
                    using var getDoc = JsonDocument.Parse(getJson);
                    if (getDoc.RootElement.GetProperty("status").GetString() == "done")
                    {
                        isDone = true;
                    }
                }
            }
            Assert.True(isDone, "Заявка мала дійти до статусу done");

            // Повторний запит з тим самим Idempotency-Key ПІСЛЯ done
            using var repeatReq = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId}/registrations");
            repeatReq.Headers.Add("Idempotency-Key", ik);
            var repeatResp = await client.SendAsync(repeatReq);

            Assert.Equal(HttpStatusCode.Accepted, repeatResp.StatusCode);
            var repeatJson = await repeatResp.Content.ReadAsStringAsync();
            using var repeatDoc = JsonDocument.Parse(repeatJson);
            Guid repeatReqId = Guid.Parse(repeatDoc.RootElement.GetProperty("id").GetString()!);
            Assert.Equal(reqId, repeatReqId);
            Assert.Equal($"/v1/enrollment-requests/{reqId}", repeatResp.Headers.Location?.ToString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Registrations_Idempotency_SameKeyDifferentParameters_Returns409Conflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId1 = "stud-reg-diff-1";
        string studentId2 = "stud-reg-diff-2";
        string groupId1 = "GRP-REG-DIFF-1";
        string groupId2 = "GRP-REG-DIFF-2";
        string discId1 = "DISC-REG-DIFF-1";
        string discId2 = "DISC-REG-DIFF-2";
        string ik = $"ik-diff-{Guid.NewGuid():N}";

        await TestEnvironment.CleanupTestDataAsync(studentId1, groupId1, discId1);
        await TestEnvironment.CleanupTestDataAsync(studentId2, groupId2, discId2);

        try
        {
            await EnsureTestStudentAsync(studentId1, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestStudentAsync(studentId2, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId1, discId1, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);
            await EnsureTestGroupAsync(groupId2, discId2, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            var client1 = _factory.CreateClient();
            client1.DefaultRequestHeaders.Add("X-User-Id", studentId1);
            client1.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-web-1-{Guid.NewGuid():N}");

            var pResp1 = await client1.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId1}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, pResp1.StatusCode);
            var pJson1 = await pResp1.Content.ReadAsStringAsync();
            using var pDoc1 = JsonDocument.Parse(pJson1);
            Guid enrollmentId1 = Guid.Parse(pDoc1.RootElement.GetProperty("id").GetString()!);

            // Перший виклик з ik для studentId1 та enrollmentId1
            using var reg1 = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId1}/registrations");
            reg1.Headers.Add("Idempotency-Key", ik);
            var rResp1 = await client1.SendAsync(reg1);
            Assert.Equal(HttpStatusCode.Accepted, rResp1.StatusCode);

            // Спроба використати той самий ik іншим студентом
            var client2 = _factory.CreateClient();
            client2.DefaultRequestHeaders.Add("X-User-Id", studentId2);
            client2.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-web-2-{Guid.NewGuid():N}");

            var pResp2 = await client2.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId2}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, pResp2.StatusCode);
            var pJson2 = await pResp2.Content.ReadAsStringAsync();
            using var pDoc2 = JsonDocument.Parse(pJson2);
            Guid enrollmentId2 = Guid.Parse(pDoc2.RootElement.GetProperty("id").GetString()!);

            using var regDiffStud = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId2}/registrations");
            regDiffStud.Headers.Add("Idempotency-Key", ik);
            var rRespDiffStud = await client2.SendAsync(regDiffStud);
            Assert.Equal(HttpStatusCode.Conflict, rRespDiffStud.StatusCode);
            var errJson = await rRespDiffStud.Content.ReadAsStringAsync();
            using var errDoc = JsonDocument.Parse(errJson);
            Assert.Equal("idempotency_conflict", errDoc.RootElement.GetProperty("error_code").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId1, groupId1, discId1);
            await TestEnvironment.CleanupTestDataAsync(studentId2, groupId2, discId2);
        }
    }

    [SkippableFact]
    public async Task Registrations_Idempotency_ConcurrentTwoRequests_OneEnqueuedOneReturnsExistingAccepted()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "stud-reg-concurrent";
        string groupId = "GRP-REG-CONCUR";
        string discId = "DISC-REG-CONCUR";
        string ik = $"ik-concur-{Guid.NewGuid():N}";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-web-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            // Два одночасні запити з однаковим Idempotency-Key
            using var clientA = _factory.CreateClient();
            clientA.DefaultRequestHeaders.Add("X-User-Id", studentId);
            using var reqA = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId}/registrations");
            reqA.Headers.Add("Idempotency-Key", ik);

            using var clientB = _factory.CreateClient();
            clientB.DefaultRequestHeaders.Add("X-User-Id", studentId);
            using var reqB = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId}/registrations");
            reqB.Headers.Add("Idempotency-Key", ik);

            var taskA = clientA.SendAsync(reqA);
            var taskB = clientB.SendAsync(reqB);

            var responses = await Task.WhenAll(taskA, taskB);
            Assert.Equal(HttpStatusCode.Accepted, responses[0].StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, responses[1].StatusCode);

            var jsonA = await responses[0].Content.ReadAsStringAsync();
            var jsonB = await responses[1].Content.ReadAsStringAsync();
            using var docA = JsonDocument.Parse(jsonA);
            using var docB = JsonDocument.Parse(jsonB);

            Guid reqIdA = Guid.Parse(docA.RootElement.GetProperty("id").GetString()!);
            Guid reqIdB = Guid.Parse(docB.RootElement.GetProperty("id").GetString()!);

            Assert.Equal(reqIdA, reqIdB);
            Assert.Equal(responses[0].Headers.Location, responses[1].Headers.Location);

            // Очікуємо завершення обробки воркером РПО
            var dpcClient = _factory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            bool isDone = false;
            for (int i = 0; i < 40 && !isDone; i++)
            {
                await Task.Delay(100);
                var getResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{reqIdA}");
                if (getResp.StatusCode == HttpStatusCode.OK)
                {
                    var getJson = await getResp.Content.ReadAsStringAsync();
                    using var getDoc = JsonDocument.Parse(getJson);
                    if (getDoc.RootElement.GetProperty("status").GetString() == "done")
                    {
                        isDone = true;
                    }
                }
            }
            Assert.True(isDone, "Конкурентна заявка мала успішно перейти у статус done");

            // Перевіряємо, що в БД створено РІВНО ОДНУ заявку з цим Idempotency-Key
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmdReqCount = new NpgsqlCommand("SELECT COUNT(*) FROM enrollment_requests WHERE idempotency_key = @ik;", conn);
                cmdReqCount.Parameters.AddWithValue("ik", ik);
                long reqCount = (long)(await cmdReqCount.ExecuteScalarAsync())!;
                Assert.Equal(1L, reqCount);

                // Перевіряємо, що у decanate_roster створено РІВНО ОДИН запис для цього Web-запису
                await using var cmdRosterCount = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE enrollment_id = @eid;", conn);
                cmdRosterCount.Parameters.AddWithValue("eid", enrollmentId);
                long rosterCount = (long)(await cmdRosterCount.ExecuteScalarAsync())!;
                Assert.Equal(1L, rosterCount);

                // Перевіряємо, що Web-запис підтверджено (confirmed) і його версія збільшилась рівно 1 раз (1 -> 2)
                await using var cmdEnr = new NpgsqlCommand("SELECT status, version FROM enrollment WHERE id = @eid;", conn);
                cmdEnr.Parameters.AddWithValue("eid", enrollmentId);
                await using var readerEnr = await cmdEnr.ExecuteReaderAsync();
                Assert.True(await readerEnr.ReadAsync());
                Assert.Equal("confirmed", readerEnr.GetString(0));
                Assert.Equal(2, readerEnr.GetInt32(1));
                await readerEnr.CloseAsync();

                // Перевіряємо, що предметний ефект списання місця в групі виконано РІВНО один раз:
                // seats = 5, початковий available = 5, після створення запису held стало 4, при внесенні в roster повторного списання немає (залишається 4)
                await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @gid;", conn);
                cmdGrp.Parameters.AddWithValue("gid", groupId);
                int available = (int)(await cmdGrp.ExecuteScalarAsync())!;
                Assert.Equal(4, available);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Registrations_Idempotency_ConcurrentTwoDifferentStudents_OneAcceptedOneConflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string student1 = "stud-cross-ik-1";
        string student2 = "stud-cross-ik-2";
        string groupId1 = "GRP-CROSS-IK-1";
        string groupId2 = "GRP-CROSS-IK-2";
        string discId1 = "DISC-CROSS-IK-1";
        string discId2 = "DISC-CROSS-IK-2";
        string sharedIk = $"ik-cross-{Guid.NewGuid():N}";

        await TestEnvironment.CleanupTestDataAsync(student1, groupId1, discId1);
        await TestEnvironment.CleanupTestDataAsync(student2, groupId2, discId2);

        try
        {
            await EnsureTestStudentAsync(student1, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestStudentAsync(student2, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId1, discId1, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);
            await EnsureTestGroupAsync(groupId2, discId2, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            // Створюємо enrollment для студента 1
            var client1 = _factory.CreateClient();
            client1.DefaultRequestHeaders.Add("X-User-Id", student1);
            client1.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-web-{Guid.NewGuid():N}");
            var postResp1 = await client1.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId1}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp1.StatusCode);
            var json1 = await postResp1.Content.ReadAsStringAsync();
            using var doc1 = JsonDocument.Parse(json1);
            Guid enrollmentId1 = Guid.Parse(doc1.RootElement.GetProperty("id").GetString()!);

            // Створюємо enrollment для студента 2
            var client2 = _factory.CreateClient();
            client2.DefaultRequestHeaders.Add("X-User-Id", student2);
            client2.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-web-{Guid.NewGuid():N}");
            var postResp2 = await client2.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId2}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp2.StatusCode);
            var json2 = await postResp2.Content.ReadAsStringAsync();
            using var doc2 = JsonDocument.Parse(json2);
            Guid enrollmentId2 = Guid.Parse(doc2.RootElement.GetProperty("id").GetString()!);

            // Одночасно відправляємо POST /v1/enrollments/{id}/registrations зі спільним Idempotency-Key для двох різних студентів
            using var regClient1 = _factory.CreateClient();
            regClient1.DefaultRequestHeaders.Add("X-User-Id", student1);
            using var regReq1 = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId1}/registrations");
            regReq1.Headers.Add("Idempotency-Key", sharedIk);

            using var regClient2 = _factory.CreateClient();
            regClient2.DefaultRequestHeaders.Add("X-User-Id", student2);
            using var regReq2 = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{enrollmentId2}/registrations");
            regReq2.Headers.Add("Idempotency-Key", sharedIk);

            var task1 = regClient1.SendAsync(regReq1);
            var task2 = regClient2.SendAsync(regReq2);

            var responses = await Task.WhenAll(task1, task2);
            var statuses = responses.Select(r => r.StatusCode).ToList();

            // Завдяки глобальному унікальному індексу idx_enrollment_requests_idempotency_global:
            // Рівно один студент отримує 202 Accepted, а другий — 409 Conflict (idempotency_conflict)
            Assert.Contains(HttpStatusCode.Accepted, statuses);
            Assert.Contains(HttpStatusCode.Conflict, statuses);

            var conflictResp = responses.First(r => r.StatusCode == HttpStatusCode.Conflict);
            var conflictJson = await conflictResp.Content.ReadAsStringAsync();
            using var conflictDoc = JsonDocument.Parse(conflictJson);
            Assert.Equal("idempotency_conflict", conflictDoc.RootElement.GetProperty("error_code").GetString());

            var acceptedResp = responses.First(r => r.StatusCode == HttpStatusCode.Accepted);
            var acceptedJson = await acceptedResp.Content.ReadAsStringAsync();
            using var acceptedDoc = JsonDocument.Parse(acceptedJson);
            Guid acceptedReqId = Guid.Parse(acceptedDoc.RootElement.GetProperty("id").GetString()!);
            string acceptedStudent = acceptedDoc.RootElement.GetProperty("student_id").GetString()!;

            // Очікуємо завершення обробки прийнятої заявки
            var dpcClient = _factory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            bool isDone = false;
            for (int i = 0; i < 40 && !isDone; i++)
            {
                await Task.Delay(100);
                var getResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{acceptedReqId}");
                if (getResp.StatusCode == HttpStatusCode.OK)
                {
                    var getJson = await getResp.Content.ReadAsStringAsync();
                    using var getDoc = JsonDocument.Parse(getJson);
                    if (getDoc.RootElement.GetProperty("status").GetString() == "done")
                    {
                        isDone = true;
                    }
                }
            }
            Assert.True(isDone, "Прийнята заявка мала успішно перейти у статус done");

            // Перевіряємо базу даних
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                // 1. Рівно ОДНА заявка в enrollment_requests з цим ключем
                await using var cmdCount = new NpgsqlCommand("SELECT COUNT(*) FROM enrollment_requests WHERE idempotency_key = @ik;", conn);
                cmdCount.Parameters.AddWithValue("ik", sharedIk);
                long reqCount = (long)(await cmdCount.ExecuteScalarAsync())!;
                Assert.Equal(1L, reqCount);

                // 2. Власник заявки відповідає прийнятому студенту
                await using var cmdOwner = new NpgsqlCommand("SELECT student_id FROM enrollment_requests WHERE idempotency_key = @ik;", conn);
                cmdOwner.Parameters.AddWithValue("ik", sharedIk);
                string ownerInDb = (string)(await cmdOwner.ExecuteScalarAsync())!;
                Assert.Equal(acceptedStudent, ownerInDb);

                // 3. У decanate_roster створено рівно один запис для цієї заявки
                await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE request_id = @rid;", conn);
                cmdRoster.Parameters.AddWithValue("rid", acceptedReqId);
                long rosterCount = (long)(await cmdRoster.ExecuteScalarAsync())!;
                Assert.Equal(1L, rosterCount);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(student1, groupId1, discId1);
            await TestEnvironment.CleanupTestDataAsync(student2, groupId2, discId2);
        }
    }
}
