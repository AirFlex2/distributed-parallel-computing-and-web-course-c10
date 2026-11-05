using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnrollmentService.Services.Processing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class E2EHttpEndpointTests
{
    [SkippableFact]
    public async Task JointScenario_NonExistentEnrollmentId_RejectedAndNoRoster()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-joint-ne";
        string groupId = "GRP-E2E-JOINT-NE";
        string discId = "DISC-E2E-JOINT-NE";
        Guid fakeEnrollmentId = Guid.NewGuid();
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    student_id = studentId,
                    group_id = groupId,
                    enrollment_id = fakeEnrollmentId.ToString("D")
                }),
                Encoding.UTF8,
                "application/json");

            var resp = await client.PostAsync("/v1/enrollment-requests", content);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            createdRequestId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);

            await WaitForTerminalStatusAsync(client, createdRequestId.Value);

            var pollResp = await client.GetAsync($"/v1/enrollment-requests/{createdRequestId.Value}");
            var pollJson = await pollResp.Content.ReadAsStringAsync();
            using var pollDoc = JsonDocument.Parse(pollJson);
            Assert.Equal("rejected", pollDoc.RootElement.GetProperty("status").GetString());

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE request_id = @id;", conn);
            cmdRoster.Parameters.AddWithValue("id", createdRequestId.Value);
            long rosterCount = (long)(await cmdRoster.ExecuteScalarAsync())!;
            Assert.Equal(0L, rosterCount);

            // Перевіряємо, що enrollment_id обнулено (FK violation), а requested_enrollment_id збережено
            await using var cmdReq = new NpgsqlCommand("SELECT enrollment_id, requested_enrollment_id FROM enrollment_requests WHERE id = @id;", conn);
            cmdReq.Parameters.AddWithValue("id", createdRequestId.Value);
            await using var reader = await cmdReq.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0), "enrollment_id має бути NULL при неіснуючому записі (FK violation)");
            Assert.Equal(fakeEnrollmentId, reader.GetGuid(1));
            await reader.CloseAsync();

            // Перевіряємо, що вільні місця в групі не зменшилися
            await using var cmdSeats = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @id;", conn);
            cmdSeats.Parameters.AddWithValue("id", groupId);
            int availableSeats = (int)(await cmdSeats.ExecuteScalarAsync())!;
            Assert.Equal(5, availableSeats);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task JointScenario_NonExistentRequestedEnrollmentId_DpcSandboxDisabled_RejectedAndNoRoster()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-joint-dis";
        string groupId = "GRP-E2E-JOINT-DIS";
        string discId = "DISC-E2E-JOINT-DIS";
        Guid fakeEnrollmentId = Guid.NewGuid();
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: false);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    student_id = studentId,
                    group_id = groupId,
                    enrollment_id = fakeEnrollmentId.ToString("D")
                }),
                Encoding.UTF8,
                "application/json");

            var resp = await client.PostAsync("/v1/enrollment-requests", content);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            createdRequestId = Guid.Parse(doc.RootElement.GetProperty("id").GetString()!);

            await WaitForTerminalStatusAsync(client, createdRequestId.Value);

            var pollResp = await client.GetAsync($"/v1/enrollment-requests/{createdRequestId.Value}");
            var pollJson = await pollResp.Content.ReadAsStringAsync();
            using var pollDoc = JsonDocument.Parse(pollJson);
            Assert.Equal("rejected", pollDoc.RootElement.GetProperty("status").GetString());

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE request_id = @id;", conn);
            cmdRoster.Parameters.AddWithValue("id", createdRequestId.Value);
            long rosterCount = (long)(await cmdRoster.ExecuteScalarAsync())!;
            Assert.Equal(0L, rosterCount);

            await using var cmdReq = new NpgsqlCommand("SELECT enrollment_id, requested_enrollment_id FROM enrollment_requests WHERE id = @id;", conn);
            cmdReq.Parameters.AddWithValue("id", createdRequestId.Value);
            await using var reader = await cmdReq.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0), "enrollment_id має бути NULL при неіснуючому записі");
            Assert.Equal(fakeEnrollmentId, reader.GetGuid(1));
            await reader.CloseAsync();

            await using var cmdSeats = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @id;", conn);
            cmdSeats.Parameters.AddWithValue("id", groupId);
            int availableSeats = (int)(await cmdSeats.ExecuteScalarAsync())!;
            Assert.Equal(5, availableSeats);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task JointScenario_MismatchedGroupId_RejectedAndNoRoster()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-joint-mg";
        string groupId1 = "GRP-E2E-JOINT-MG1";
        string discId1 = "DISC-E2E-JOINT-MG1";
        string groupId2 = "GRP-E2E-JOINT-MG2";
        string discId2 = "DISC-E2E-JOINT-MG2";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId1, discId1);
        await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId2);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId1, discId1, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);
            await EnsureTestGroupAsync(groupId2, discId2, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            // Створюємо Web-запис у групі 1
            var webClient = _factory.CreateClient();
            webClient.DefaultRequestHeaders.Add("X-User-Id", studentId);
            webClient.DefaultRequestHeaders.Add("Idempotency-Key", $"web-idem-{Guid.NewGuid():N}");

            var webResp = await webClient.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId1}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, webResp.StatusCode);
            var webJson = await webResp.Content.ReadAsStringAsync();
            using var webDoc = JsonDocument.Parse(webJson);
            Guid heldEnrollmentId = Guid.Parse(webDoc.RootElement.GetProperty("id").GetString()!);

            // Надсилаємо DPC-заявку для групи 2, але вказуємо enrollment_id від групи 1
            var dpcClient = _factory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var dpcResp = await dpcClient.PostAsync("/v1/enrollment-requests", new StringContent(
                JsonSerializer.Serialize(new
                {
                    student_id = studentId,
                    group_id = groupId2,
                    enrollment_id = heldEnrollmentId.ToString("D")
                }),
                Encoding.UTF8,
                "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, dpcResp.StatusCode);

            var dpcJson = await dpcResp.Content.ReadAsStringAsync();
            using var dpcDoc = JsonDocument.Parse(dpcJson);
            createdRequestId = Guid.Parse(dpcDoc.RootElement.GetProperty("id").GetString()!);

            await WaitForTerminalStatusAsync(dpcClient, createdRequestId.Value);

            var pollResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{createdRequestId.Value}");
            var pollJson = await pollResp.Content.ReadAsStringAsync();
            using var pollDoc = JsonDocument.Parse(pollJson);
            Assert.Equal("rejected", pollDoc.RootElement.GetProperty("status").GetString());

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var checkReq = new NpgsqlCommand("SELECT rejection_reason FROM enrollment_requests WHERE id = @id;", conn);
            checkReq.Parameters.AddWithValue("id", createdRequestId.Value);
            string reason = (string)(await checkReq.ExecuteScalarAsync())!;
            Assert.Contains(groupId1, reason);
            Assert.Contains(groupId2, reason);

            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE request_id = @id;", conn);
            cmdRoster.Parameters.AddWithValue("id", createdRequestId.Value);
            long rosterCount = (long)(await cmdRoster.ExecuteScalarAsync())!;
            Assert.Equal(0L, rosterCount);

            // Перевіряємо, що Web-запис для групи 1 не було підтверджено
            await using var checkEnr = new NpgsqlCommand("SELECT status FROM enrollment WHERE id = @id;", conn);
            checkEnr.Parameters.AddWithValue("id", heldEnrollmentId);
            string enrStatus = (string)(await checkEnr.ExecuteScalarAsync())!;
            Assert.Equal("held", enrStatus);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId1, discId1, createdRequestId);
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId2, discId2);
        }
    }

    [SkippableFact]
    public async Task JointScenario_MismatchedStudentOrGroup_RejectedAndNoRoster()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string student1 = "student-e2e-joint-m1";
        string student2 = "student-e2e-joint-m2";
        string groupId = "GRP-E2E-JOINT-M";
        string discId = "DISC-E2E-JOINT-M";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(student1, groupId, discId);
        await TestEnvironment.CleanupTestDataAsync(student2, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(student1, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestStudentAsync(student2, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            // Створюємо Web-запис для student1
            var webClient = _factory.CreateClient();
            webClient.DefaultRequestHeaders.Add("X-User-Id", student1);
            webClient.DefaultRequestHeaders.Add("Idempotency-Key", $"web-idem-{Guid.NewGuid():N}");

            var webContent = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var webResp = await webClient.PostAsync("/v1/enrollments", webContent);
            Assert.Equal(HttpStatusCode.Created, webResp.StatusCode);
            var webJson = await webResp.Content.ReadAsStringAsync();
            using var webDoc = JsonDocument.Parse(webJson);
            Guid heldEnrollmentId = Guid.Parse(webDoc.RootElement.GetProperty("id").GetString()!);

            // Надсилаємо DPC-заявку для student2, але вказуємо enrollment_id від student1
            var dpcClient = _factory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var dpcContent = new StringContent(
                JsonSerializer.Serialize(new
                {
                    student_id = student2,
                    group_id = groupId,
                    enrollment_id = heldEnrollmentId.ToString("D")
                }),
                Encoding.UTF8,
                "application/json");

            var dpcResp = await dpcClient.PostAsync("/v1/enrollment-requests", dpcContent);
            Assert.Equal(HttpStatusCode.Accepted, dpcResp.StatusCode);

            var dpcJson = await dpcResp.Content.ReadAsStringAsync();
            using var dpcDoc = JsonDocument.Parse(dpcJson);
            createdRequestId = Guid.Parse(dpcDoc.RootElement.GetProperty("id").GetString()!);

            await WaitForTerminalStatusAsync(dpcClient, createdRequestId.Value);

            var pollResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{createdRequestId.Value}");
            var pollJson = await pollResp.Content.ReadAsStringAsync();
            using var pollDoc = JsonDocument.Parse(pollJson);
            Assert.Equal("rejected", pollDoc.RootElement.GetProperty("status").GetString());

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE request_id = @id;", conn);
            cmdRoster.Parameters.AddWithValue("id", createdRequestId.Value);
            long rosterCount = (long)(await cmdRoster.ExecuteScalarAsync())!;
            Assert.Equal(0L, rosterCount);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(student1, groupId, discId, createdRequestId);
            await TestEnvironment.CleanupTestDataAsync(student2, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task JointScenario_AlreadyConfirmedEnrollment_RejectedAndNoRoster()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-joint-ac";
        string groupId = "GRP-E2E-JOINT-AC";
        string discId = "DISC-E2E-JOINT-AC";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            var webClient = _factory.CreateClient();
            webClient.DefaultRequestHeaders.Add("X-User-Id", studentId);
            webClient.DefaultRequestHeaders.Add("Idempotency-Key", $"web-idem-{Guid.NewGuid():N}");

            var webResp = await webClient.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, webResp.StatusCode);
            var webJson = await webResp.Content.ReadAsStringAsync();
            using var webDoc = JsonDocument.Parse(webJson);
            Guid heldEnrollmentId = Guid.Parse(webDoc.RootElement.GetProperty("id").GetString()!);

            // Підтверджуємо запис через Web (held -> confirmed)
            var confResp = await webClient.PostAsync($"/v1/enrollments/{heldEnrollmentId}/confirm", null);
            Assert.Equal(HttpStatusCode.OK, confResp.StatusCode);

            // Тепер передаємо цей вже підтверджений запис у DPC
            var dpcClient = _factory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var dpcResp = await dpcClient.PostAsync("/v1/enrollment-requests", new StringContent(
                JsonSerializer.Serialize(new
                {
                    student_id = studentId,
                    group_id = groupId,
                    enrollment_id = heldEnrollmentId.ToString("D")
                }),
                Encoding.UTF8,
                "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, dpcResp.StatusCode);

            var dpcJson = await dpcResp.Content.ReadAsStringAsync();
            using var dpcDoc = JsonDocument.Parse(dpcJson);
            createdRequestId = Guid.Parse(dpcDoc.RootElement.GetProperty("id").GetString()!);

            await WaitForTerminalStatusAsync(dpcClient, createdRequestId.Value);

            var pollResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{createdRequestId.Value}");
            var pollJson = await pollResp.Content.ReadAsStringAsync();
            using var pollDoc = JsonDocument.Parse(pollJson);
            Assert.Equal("rejected", pollDoc.RootElement.GetProperty("status").GetString());

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE request_id = @id;", conn);
            cmdRoster.Parameters.AddWithValue("id", createdRequestId.Value);
            long rosterCount = (long)(await cmdRoster.ExecuteScalarAsync())!;
            Assert.Equal(0L, rosterCount);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task JointScenario_Success_WebHeld_ThenConfirmedByDpcWorker()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-joint-ok";
        string groupId = "GRP-E2E-JOINT-OK";
        string discId = "DISC-E2E-JOINT-OK";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            var webClient = _factory.CreateClient();
            webClient.DefaultRequestHeaders.Add("X-User-Id", studentId);
            webClient.DefaultRequestHeaders.Add("Idempotency-Key", $"web-idem-{Guid.NewGuid():N}");

            var webResp = await webClient.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, webResp.StatusCode);
            var webJson = await webResp.Content.ReadAsStringAsync();
            using var webDoc = JsonDocument.Parse(webJson);
            Guid heldEnrollmentId = Guid.Parse(webDoc.RootElement.GetProperty("id").GetString()!);
            Assert.Equal("held", webDoc.RootElement.GetProperty("status").GetString());

            // Передаємо enrollment_id до черги РПО
            var dpcClient = _factory.CreateClient();
            dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var dpcResp = await dpcClient.PostAsync("/v1/enrollment-requests", new StringContent(
                JsonSerializer.Serialize(new
                {
                    student_id = studentId,
                    group_id = groupId,
                    enrollment_id = heldEnrollmentId.ToString("D")
                }),
                Encoding.UTF8,
                "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, dpcResp.StatusCode);

            var dpcJson = await dpcResp.Content.ReadAsStringAsync();
            using var dpcDoc = JsonDocument.Parse(dpcJson);
            createdRequestId = Guid.Parse(dpcDoc.RootElement.GetProperty("id").GetString()!);

            await WaitForTerminalStatusAsync(dpcClient, createdRequestId.Value);

            var pollResp = await dpcClient.GetAsync($"/v1/enrollment-requests/{createdRequestId.Value}");
            var pollJson = await pollResp.Content.ReadAsStringAsync();
            using var pollDoc = JsonDocument.Parse(pollJson);
            Assert.Equal("done", pollDoc.RootElement.GetProperty("status").GetString());

            await using var conn = await TestEnvironment.OpenConnectionAsync();
            // Перевіряємо, що запис enrollment перейшов у стан confirmed
            await using var cmdEnr = new NpgsqlCommand("SELECT status FROM enrollment WHERE id = @id;", conn);
            cmdEnr.Parameters.AddWithValue("id", heldEnrollmentId);
            string enrStatus = (string)(await cmdEnr.ExecuteScalarAsync())!;
            Assert.Equal("confirmed", enrStatus);

            // Перевіряємо запис у decanate_roster
            await using var cmdRoster = new NpgsqlCommand("SELECT assignment_type, enrollment_id FROM decanate_roster WHERE request_id = @id;", conn);
            cmdRoster.Parameters.AddWithValue("id", createdRequestId.Value);
            await using var reader = await cmdRoster.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("joint_web_enrollment", reader.GetString(0));
            Assert.Equal(heldEnrollmentId, reader.GetGuid(1));
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task JointScenario_EnrollmentDeletedBeforeProcessing_RejectedByRequestedEnrollmentId()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-del-before";
        string groupId = "GRP-E2E-DEL-BEFORE";
        string discId = "DISC-E2E-DEL-BEFORE";
        Guid heldEnrollmentId = Guid.NewGuid();
        Guid testRequestId = Guid.NewGuid();

        var processor = _factory.Services.GetRequiredService<EnrollmentRequestProcessor>();
        var requestReachedWorker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowProcessingToProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Реєструємо строго ізольований перехоплювач для конкретного testRequestId:
        // Жоден інший запит у системі чи паралельному тесті не буде зупинено або заблоковано.
        using var hookRegistration = processor.RegisterPreProcessingHook(testRequestId, async (item) =>
        {
            requestReachedWorker.TrySetResult(true);
            await allowProcessingToProceed.Task;
        });

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, testRequestId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 4, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            await using var conn = await TestEnvironment.OpenConnectionAsync();

            // 1. Створюємо запис enrollment у стані held
            await using (var insEnr = new NpgsqlCommand(@"
                INSERT INTO enrollment (id, student_id, group_id, discipline_id, status, version, created_at, expires_at)
                VALUES (@id, @sid, @gid, @did, 'held', 1, NOW(), NOW() + INTERVAL '15 minutes');", conn))
            {
                insEnr.Parameters.AddWithValue("id", heldEnrollmentId);
                insEnr.Parameters.AddWithValue("sid", studentId);
                insEnr.Parameters.AddWithValue("gid", groupId);
                insEnr.Parameters.AddWithValue("did", discId);
                await insEnr.ExecuteNonQueryAsync();
            }

            // 2. Створюємо пов'язаний DPC-запит через API
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    student_id = studentId,
                    group_id = groupId,
                    request_id = testRequestId,
                    enrollment_id = heldEnrollmentId.ToString("D")
                }),
                Encoding.UTF8,
                "application/json");

            var resp = await client.PostAsync("/v1/enrollment-requests", content);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            // 3. Переконуємося, що запит збережено в черзі, але ще не оброблено (воркер зупинений на точці синхронізації)
            await requestReachedWorker.Task;

            await using (var checkQueued = new NpgsqlCommand("SELECT status FROM enrollment_requests WHERE id = @id;", conn))
            {
                checkQueued.Parameters.AddWithValue("id", testRequestId);
                string currentStatus = (string)(await checkQueued.ExecuteScalarAsync())!;
                Assert.Equal("queued", currentStatus);
            }

            // 4. Видаляємо Web-запис до початку виконання обробки
            await using (var delEnr = new NpgsqlCommand("DELETE FROM enrollment WHERE id = @id;", conn))
            {
                delEnr.Parameters.AddWithValue("id", heldEnrollmentId);
                int deleted = await delEnr.ExecuteNonQueryAsync();
                Assert.Equal(1, deleted);
            }

            // Перевіряємо, що каскадний FK ON DELETE SET NULL обнулив enrollment_id,
            // але requested_enrollment_id зберіг первинний GUID клієнта
            await using (var checkReq = new NpgsqlCommand(@"
                SELECT enrollment_id, requested_enrollment_id
                FROM enrollment_requests
                WHERE id = @reqId;", conn))
            {
                checkReq.Parameters.AddWithValue("reqId", testRequestId);
                await using var reader = await checkReq.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0), "enrollment_id мав бути обнулений через ON DELETE SET NULL");
                Assert.Equal(heldEnrollmentId, reader.GetGuid(1));
            }

            // 5. Тільки після цього дозволяємо воркеру обробку запиту
            allowProcessingToProceed.TrySetResult(true);

            // 6. Очікуємо завершення обробки фоновим воркером
            await WaitForTerminalStatusAsync(client, testRequestId);

            // 7. Перевіряємо, що заявка відхилена, а не пішла в автономну обробку
            await using (var finalCheck = new NpgsqlCommand(@"
                SELECT status, rejection_reason
                FROM enrollment_requests
                WHERE id = @reqId;", conn))
            {
                finalCheck.Parameters.AddWithValue("reqId", testRequestId);
                await using var reader = await finalCheck.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("rejected", reader.GetString(0));
                Assert.Contains("не знайдено або було видалено", reader.GetString(1));
            }

            // 8. Перевіряємо, що в decanate_roster нічого не створено
            await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*) FROM decanate_roster WHERE request_id = @id;", conn);
            {
                cmdRoster.Parameters.AddWithValue("id", testRequestId);
                long count = (long)(await cmdRoster.ExecuteScalarAsync())!;
                Assert.Equal(0L, count);
            }
        }
        finally
        {
            // Гарантуємо негайне звільнення фонового воркера навіть при виключеннях або збоях Assert
            allowProcessingToProceed.TrySetResult(true);
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, testRequestId);
        }
    }

    [SkippableFact]
    public async Task Enrollment_AlreadyDirectlyConfirmed_SubmittingToRegistrations_Returns409Conflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "stud-reg-already-conf";
        string groupId = "GRP-REG-ALREADY-CONF";
        string discId = "DISC-REG-ALREADY-CONF";

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

            // Пряме підтвердження студентом через POST /confirm (Web О1.1: held -> confirmed)
            var confResp = await client.PostAsync($"/v1/enrollments/{enrollmentId}/confirm", null);
            Assert.Equal(HttpStatusCode.OK, confResp.StatusCode);

            // Спроба відправити вже підтверджений запис до черги РПО decanate_roster
            var regResp = await client.PostAsync($"/v1/enrollments/{enrollmentId}/registrations", null);
            Assert.Equal(HttpStatusCode.Conflict, regResp.StatusCode);
            var regErrJson = await regResp.Content.ReadAsStringAsync();
            using var regErrDoc = JsonDocument.Parse(regErrJson);
            Assert.Equal("enrollment_not_held", regErrDoc.RootElement.GetProperty("error_code").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }
}
