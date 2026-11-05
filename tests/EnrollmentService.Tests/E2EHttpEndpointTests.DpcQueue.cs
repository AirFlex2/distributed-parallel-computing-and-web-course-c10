using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class E2EHttpEndpointTests
{
    [SkippableFact]
    public async Task DpcQueue_EndToEnd_PostAcceptedAndFirstReadTracksT4()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-dpc-test";
        string groupId = "GRP-E2E-DPC-G1";
        string discId = "DISC-E2E-DPC";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 10,
                available: 10,
                opensAt: DateTimeOffset.UtcNow.AddDays(-1),
                closesAt: DateTimeOffset.UtcNow.AddDays(1));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            // 1. POST /v1/enrollment-requests повертає 202 Accepted
            var postBody = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                Encoding.UTF8,
                "application/json");

            var postResp = await client.PostAsync("/v1/enrollment-requests", postBody);
            Assert.Equal(HttpStatusCode.Accepted, postResp.StatusCode);
            Assert.NotNull(postResp.Headers.Location);

            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            string requestIdStr = postDoc.RootElement.GetProperty("id").GetString()!;
            createdRequestId = Guid.Parse(requestIdStr);

            // 2. Ранній опрос (polling) до завершення обробки:
            // Якщо заявка все ще в queued або processing, first_read_at обов'язково null (О1.5 DPC)
            var getRespEarly = await client.GetAsync($"/v1/enrollment-requests/{requestIdStr}");
            Assert.Equal(HttpStatusCode.OK, getRespEarly.StatusCode);
            var getEarlyJson = await getRespEarly.Content.ReadAsStringAsync();
            using var getEarlyDoc = JsonDocument.Parse(getEarlyJson);
            string earlyStatus = getEarlyDoc.RootElement.GetProperty("status").GetString()!;
            if (earlyStatus is "queued" or "processing")
            {
                Assert.True(getEarlyDoc.RootElement.TryGetProperty("first_read_at", out var earlyProp));
                Assert.Equal(JsonValueKind.Null, earlyProp.ValueKind);
            }

            // 3. Очікуємо досягнення кінцевого статусу заявки фоновим воркером із тайм-аутом
            string finalStatus = "";
            JsonElement finalDocRoot = default;
            var stopwatch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(5);

            while (stopwatch.Elapsed < timeout)
            {
                var pollResp = await client.GetAsync($"/v1/enrollment-requests/{requestIdStr}");
                if (pollResp.IsSuccessStatusCode)
                {
                    var pollJson = await pollResp.Content.ReadAsStringAsync();
                    var pollDoc = JsonDocument.Parse(pollJson);
                    finalDocRoot = pollDoc.RootElement.Clone();
                    finalStatus = finalDocRoot.GetProperty("status").GetString() ?? "";

                    if (finalStatus is "done" or "rejected" or "failed")
                    {
                        break;
                    }
                }
                await Task.Delay(50);
            }

            // Перевіряємо успішне виконання заявки фоновим процесором
            Assert.Equal("done", finalStatus);
            Assert.True(finalDocRoot.TryGetProperty("finished_at", out var finProp) && finProp.GetString() != null);
            Assert.True(finalDocRoot.TryGetProperty("recorded_in_roster", out var rosterProp) && rosterProp.GetBoolean());
            Assert.True(finalDocRoot.TryGetProperty("assigned_group_id", out var grpProp) && grpProp.GetString() == groupId);

            // 4. Перший GET кінцевого результату ('done') атомарно фіксує first_read_at (t4) (О1.5 DPC)
            Assert.True(finalDocRoot.TryGetProperty("first_read_at", out var firstReadProp));
            Assert.NotNull(firstReadProp.GetString());
            string initialFirstRead = firstReadProp.GetString()!;

            // 5. Повторний GET не повинен змінювати t4
            var getRespRepeat = await client.GetAsync($"/v1/enrollment-requests/{requestIdStr}");
            Assert.Equal(HttpStatusCode.OK, getRespRepeat.StatusCode);
            var getRepeatJson = await getRespRepeat.Content.ReadAsStringAsync();
            using var getRepeatDoc = JsonDocument.Parse(getRepeatJson);
            string repeatFirstRead = getRepeatDoc.RootElement.GetProperty("first_read_at").GetString()!;
            Assert.Equal(initialFirstRead, repeatFirstRead);

            // delivery_wait_ms зафіксовано коректно і не обнулено раннім опитуванням
            if (getRepeatDoc.RootElement.TryGetProperty("delivery_wait_ms", out var delivProp) && delivProp.ValueKind != JsonValueKind.Null)
            {
                Assert.True(delivProp.GetDouble() >= 0.0);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task DpcQueue_WebGroupRequest_IsRejectedWith422_AndLeavesSeatsAndStudentStatsUnmodified()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-web-guard";
        string groupId = "DISC-WEB-01"; // Інтерактивна група Web-контуру (dpc_sandbox_enabled = false)

        await using (var conn = await TestEnvironment.OpenConnectionAsync())
        {
            await using var cmdClean = new NpgsqlCommand("DELETE FROM student WHERE id = @id;", conn);
            cmdClean.Parameters.AddWithValue("id", studentId);
            await cmdClean.ExecuteNonQueryAsync();
        }

        try
        {
            // Створюємо студента
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);

            // Фіксуємо початковий стан групи DISC-WEB-01 (без зміни даних штатної групи)
            int initialAvailable;
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
                cmdGrp.Parameters.AddWithValue("grpId", groupId);
                initialAvailable = (int)(await cmdGrp.ExecuteScalarAsync())!;
            }

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            // Подаємо асинхронну заявку на групу Web-контуру через DPC чергу
            var postContent = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/v1/enrollment-requests", postContent);

            // Очікуємо відхилення 422 Unprocessable Content
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(422, root.GetProperty("status").GetInt32());
            Assert.Equal("dpc_sandbox_disabled", root.GetProperty("error_code").GetString());

            // Перевіряємо базу даних: кількість місць у групі та статистика студента НЕ змінилися
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
                cmdGrp.Parameters.AddWithValue("grpId", groupId);
                int currentAvailable = (int)(await cmdGrp.ExecuteScalarAsync())!;
                Assert.Equal(initialAvailable, currentAvailable);

                await using var cmdStud = new NpgsqlCommand("SELECT active_count, active_ects FROM student WHERE id = @id;", conn);
                cmdStud.Parameters.AddWithValue("id", studentId);
                await using (var reader = await cmdStud.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(0, reader.GetInt32(0));
                    Assert.Equal(0.0m, reader.GetDecimal(1));
                }

                await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*)::INT FROM decanate_roster WHERE student_id = @id;", conn);
                cmdRoster.Parameters.AddWithValue("id", studentId);
                int rosterCount = (int)(await cmdRoster.ExecuteScalarAsync())!;
                Assert.Equal(0, rosterCount);
            }
        }
        finally
        {
            await using var conn = await TestEnvironment.OpenConnectionAsync();
            await using var cmdClean = new NpgsqlCommand("DELETE FROM student WHERE id = @id;", conn);
            cmdClean.Parameters.AddWithValue("id", studentId);
            await cmdClean.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task DpcQueue_SandboxGroup_AcceptsRequestAndDecrementsAvailable()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-sandbox-dpc";
        string groupId = "GRP-E2E-DPC-01";
        string discId = "DISC-E2E-DPC";
        Guid? createdRequestId = null;

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 10,
                available: 10,
                opensAt: DateTimeOffset.UtcNow.AddDays(-1),
                closesAt: DateTimeOffset.UtcNow.AddDays(1),
                ects: 3.0m,
                dpcSandboxEnabled: true);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

            var postContent = new StringContent(
                JsonSerializer.Serialize(new { student_id = studentId, group_id = groupId }),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/v1/enrollment-requests", postContent);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.NotNull(response.Headers.Location);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            string reqIdStr = doc.RootElement.GetProperty("id").GetString()!;
            createdRequestId = Guid.Parse(reqIdStr);

            // Очікуємо переходу заявки у кінцевий статус
            await WaitForTerminalStatusAsync(client, createdRequestId.Value);

            // Перевіряємо статус заявки
            var statusResp = await client.GetAsync($"/v1/enrollment-requests/{createdRequestId.Value}");
            statusResp.EnsureSuccessStatusCode();
            var statusJson = await statusResp.Content.ReadAsStringAsync();
            using var statusDoc = JsonDocument.Parse(statusJson);
            Assert.Equal("done", statusDoc.RootElement.GetProperty("status").GetString());

            // Перевіряємо базу даних: запис у decanate_roster з'явився, available зменшився на 1 (10 -> 9)
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmdRoster = new NpgsqlCommand("SELECT COUNT(*)::INT FROM decanate_roster WHERE request_id = @reqId;", conn);
                cmdRoster.Parameters.AddWithValue("reqId", createdRequestId.Value);
                int rosterCount = (int)(await cmdRoster.ExecuteScalarAsync())!;
                Assert.Equal(1, rosterCount);

                await using var cmdGrp = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @grpId;", conn);
                cmdGrp.Parameters.AddWithValue("grpId", groupId);
                int currentAvailable = (int)(await cmdGrp.ExecuteScalarAsync())!;
                Assert.Equal(9, currentAvailable);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId, createdRequestId);
        }
    }

    [SkippableFact]
    public async Task Web_RegistrationsEndpoint_HeldEnrollment_QueuesAndReturns202Accepted()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-web-reg";
        string groupId = "GRP-WEB-REG";
        string discId = "DISC-WEB-REG";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), dpcSandboxEnabled: true);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-web-reg-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            // Викликаємо POST /v1/enrollments/{id}/registrations (Web C10 стор. 37)
            var regResp = await client.PostAsync($"/v1/enrollments/{enrollmentId}/registrations", null);
            Assert.Equal(HttpStatusCode.Accepted, regResp.StatusCode);
            Assert.NotNull(regResp.Headers.Location);

            var regJson = await regResp.Content.ReadAsStringAsync();
            using var regDoc = JsonDocument.Parse(regJson);
            Guid reqId = Guid.Parse(regDoc.RootElement.GetProperty("id").GetString()!);

            // Очікуємо завершення обробки воркером (використовуючи клієнт із Bearer-токеном для маршрутів РПО)
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
            Assert.True(isDone, "Заявка мала перейти у статус done");

            // Перевіряємо, що Web-запис атомарно підтверджено (held -> confirmed)
            var enrResp = await client.GetAsync($"/v1/enrollments/{enrollmentId}");
            Assert.Equal(HttpStatusCode.OK, enrResp.StatusCode);
            var enrJson = await enrResp.Content.ReadAsStringAsync();
            using var enrDoc = JsonDocument.Parse(enrJson);
            Assert.Equal("confirmed", enrDoc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }
}
