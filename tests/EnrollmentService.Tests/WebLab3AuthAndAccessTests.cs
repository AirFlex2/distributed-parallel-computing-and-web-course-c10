using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class E2EHttpEndpointTests
{
    [SkippableFact]
    public async Task Web_O31_LoginOnAppA_WorksOnAppB_AndLogoutRevokesAcrossBothInstances()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        using var factoryA = _factory.WithWebHostBuilder(b => b.UseSetting("INSTANCE_ID", "app-a"));
        using var factoryB = _factory.WithWebHostBuilder(b => b.UseSetting("INSTANCE_ID", "app-b"));
        using var clientA = factoryA.CreateClient();
        using var clientB = factoryB.CreateClient();

        // 1. Вхід через app-a
        string token = await LoginWebAsync(clientA, "student-1", "StudentPass2026!");
        Assert.StartsWith("wbt_", token, StringComparison.Ordinal);

        // 2. Використання того самого непрозорого токена на app-b (GET /v1/me)
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var meOnB = await clientB.GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.OK, meOnB.StatusCode);
        using var meDoc = JsonDocument.Parse(await meOnB.Content.ReadAsStringAsync());
        Assert.Equal("student-1", meDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal("user", meDoc.RootElement.GetProperty("role").GetString());

        // 3. Вихід (POST /v1/auth/logout) на app-a відкликає токен на обох примірниках
        using var logoutResp = await clientA.PostAsync("/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logoutResp.StatusCode);

        using var meAfterLogoutB = await clientB.GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfterLogoutB.StatusCode);

        using var meAfterLogoutA = await clientA.GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfterLogoutA.StatusCode);
    }

    [SkippableFact]
    public async Task Web_O31_WrongPassword_ExpiredToken_And_XUserIdSpoofIgnored()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string groupId = $"GRP-O31-SPOOF-{Guid.NewGuid():N}"[..28];
        string discId = $"DISC-O31-SPOOF-{Guid.NewGuid():N}"[..28];
        string expiredRawToken = $"wbt_expired_{Guid.NewGuid():N}";
        string expiredHash = EnrollmentService.Services.WebAuthService.HashToken(expiredRawToken);

        await EnsureTestGroupAsync(
            groupId,
            discId,
            seats: 5,
            available: 5,
            opensAt: DateTimeOffset.UtcNow.AddDays(-1),
            closesAt: DateTimeOffset.UtcNow.AddDays(1));

        try
        {
            using var anonClient = _factory.CreateClient();

            // 1. Неправильний пароль -> 401
            using var badLoginResp = await anonClient.PostAsync(
                "/v1/auth/login",
                new StringContent("{\"login\":\"student-1\",\"password\":\"WrongPassword!\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, badLoginResp.StatusCode);

            // 2. Запит із простроченим токеном -> 401
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO web_auth_tokens (token_hash, user_id, username, role, student_id, created_at, expires_at, revoked_at)
                    VALUES (@hash, 'student-1', 'student-1', 'user', 'student-1', NOW() - INTERVAL '2 hours', NOW() - INTERVAL '5 minutes', NULL);", conn);
                cmd.Parameters.AddWithValue("hash", expiredHash);
                await cmd.ExecuteNonQueryAsync();
            }

            using var expiredClient = _factory.CreateClient();
            expiredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expiredRawToken);
            using var expiredResp = await expiredClient.GetAsync("/v1/me");
            Assert.Equal(HttpStatusCode.Unauthorized, expiredResp.StatusCode);

            // 3. Підміна X-User-Id ігнорується — власник визначається виключно за Bearer-токеном
            using var spoofClient = await CreateAuthenticatedWebClientAsync("student-1");
            spoofClient.DefaultRequestHeaders.Add("X-User-Id", "student-2");

            using var meResp = await spoofClient.GetAsync("/v1/me");
            Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
            using var meDoc = JsonDocument.Parse(await meResp.Content.ReadAsStringAsync());
            Assert.Equal("student-1", meDoc.RootElement.GetProperty("id").GetString());

            using var postReq = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
            postReq.Headers.Add("Idempotency-Key", $"o31-spoof-{Guid.NewGuid():N}");
            postReq.Content = new StringContent(JsonSerializer.Serialize(new { group_id = groupId }), Encoding.UTF8, "application/json");
            using var postResp = await spoofClient.SendAsync(postReq);
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            using var postDoc = JsonDocument.Parse(await postResp.Content.ReadAsStringAsync());
            Assert.Equal("student-1", postDoc.RootElement.GetProperty("student_id").GetString());
        }
        finally
        {
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var delTok = new NpgsqlCommand("DELETE FROM web_auth_tokens WHERE token_hash = @hash;", conn);
                delTok.Parameters.AddWithValue("hash", expiredHash);
                await delTok.ExecuteNonQueryAsync();
            }

            await TestEnvironment.CleanupTestDataAsync("student-1", groupId, discId);
        }
    }

    [SkippableFact]
    public async Task Web_O31_StrictSeparation_BetweenWebTokensAndDpcServiceToken()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        // 1. Служебний DPC токен відхиляється з 401 на Web-маршрутах
        using var dpcClient = _factory.CreateClient();
        dpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await dpcClient.GetAsync("/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await dpcClient.PostAsync("/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await dpcClient.GetAsync("/v1/enrollments")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await dpcClient.GetAsync("/v1/students")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await dpcClient.PostAsync("/v1/groups/DISC-WEB-01/close", null)).StatusCode);

        using var postWebReq = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
        postWebReq.Headers.Add("Idempotency-Key", $"dpc-on-web-{Guid.NewGuid():N}");
        postWebReq.Content = new StringContent("{\"group_id\":\"DISC-WEB-01\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await dpcClient.SendAsync(postWebReq)).StatusCode);

        // 2. Користувацькі Web-токени (user і staff) відхиляються з 401 на службових маршрутах РПО
        using var studentClient = await CreateAuthenticatedWebClientAsync("student-1");
        using var staffClient = await CreateAuthenticatedWebClientAsync("staff-1");

        foreach (var webClient in new[] { studentClient, staffClient })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await webClient.GetAsync("/metrics")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await webClient.PostAsync("/v1/admin/reset", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await webClient.GetAsync("/v1/group-seats")).StatusCode);

            using var recvReq = new HttpRequestMessage(HttpMethod.Post, "/v1/internal/ray-effect-receiver")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            Assert.Equal(HttpStatusCode.Unauthorized, (await webClient.SendAsync(recvReq)).StatusCode);
        }
    }

    [SkippableFact]
    public async Task Web_O32_ObjectOwnership_ListIsolation_404Parity_PatchIfMatch_Body422_StaffClose()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentA = $"stud-o32-a-{Guid.NewGuid():N}"[..24];
        string studentB = $"stud-o32-b-{Guid.NewGuid():N}"[..24];
        string group1 = $"GRP-O32-1-{Guid.NewGuid():N}"[..24];
        string disc1 = $"DISC-O32-1-{Guid.NewGuid():N}"[..24];
        string group2 = $"GRP-O32-2-{Guid.NewGuid():N}"[..24];
        string disc2 = $"DISC-O32-2-{Guid.NewGuid():N}"[..24];
        string group3 = $"GRP-O32-3-{Guid.NewGuid():N}"[..24];
        string disc3 = $"DISC-O32-3-{Guid.NewGuid():N}"[..24];

        try
        {
            await EnsureTestStudentAsync(studentA, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestStudentAsync(studentB, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(group1, disc1, seats: 5, available: 5, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), ects: 3.0m);
            await EnsureTestGroupAsync(group2, disc2, seats: 5, available: 5, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);
            await EnsureTestGroupAsync(group3, disc3, seats: 5, available: 5, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), ects: 3.0m);

            using var clientA = await CreateAuthenticatedWebClientAsync(studentA);
            using var clientB = await CreateAuthenticatedWebClientAsync(studentB);
            using var staffClient = await CreateAuthenticatedWebClientAsync("staff-1");
            using var anonClient = _factory.CreateClient();

            // 1. studentA створює власний запис у group1
            using var postA = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
            postA.Headers.Add("Idempotency-Key", $"o32-create-{Guid.NewGuid():N}");
            postA.Content = new StringContent(JsonSerializer.Serialize(new { group_id = group1 }), Encoding.UTF8, "application/json");
            using var postAResp = await clientA.SendAsync(postA);
            Assert.Equal(HttpStatusCode.Created, postAResp.StatusCode);
            using var postADoc = JsonDocument.Parse(await postAResp.Content.ReadAsStringAsync());
            string enrollmentId = postADoc.RootElement.GetProperty("id").GetString()!;

            // 2. Список GET /v1/enrollments для studentB не містить запису studentA
            using var listBResp = await clientB.GetAsync("/v1/enrollments");
            Assert.Equal(HttpStatusCode.OK, listBResp.StatusCode);
            using var listBDoc = JsonDocument.Parse(await listBResp.Content.ReadAsStringAsync());
            foreach (var item in listBDoc.RootElement.GetProperty("items").EnumerateArray())
            {
                Assert.NotEqual(enrollmentId, item.GetProperty("id").GetString());
            }

            // 3. Однаковий 404 Not Found для чужого і неіснуючого запису (GET, PATCH, confirm, cancel, registrations)
            string missingId = Guid.NewGuid().ToString("D");
            foreach (var targetId in new[] { enrollmentId, missingId })
            {
                using var getRes = await clientB.GetAsync($"/v1/enrollments/{targetId}");
                Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);
                using var getDoc = JsonDocument.Parse(await getRes.Content.ReadAsStringAsync());
                Assert.Equal("enrollment_not_found", getDoc.RootElement.GetProperty("error_code").GetString());

                using var patchRes = await clientB.PatchAsync(
                    $"/v1/enrollments/{targetId}",
                    new StringContent(JsonSerializer.Serialize(new { group_id = group2 }), Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.NotFound, patchRes.StatusCode);

                using var confirmRes = await clientB.PostAsync($"/v1/enrollments/{targetId}/confirm", null);
                Assert.Equal(HttpStatusCode.NotFound, confirmRes.StatusCode);

                using var cancelRes = await clientB.PostAsync($"/v1/enrollments/{targetId}/cancel", null);
                Assert.Equal(HttpStatusCode.NotFound, cancelRes.StatusCode);

                using var regReq = new HttpRequestMessage(HttpMethod.Post, $"/v1/enrollments/{targetId}/registrations");
                regReq.Headers.Add("Idempotency-Key", $"o32-reg-404-{Guid.NewGuid():N}");
                using var regRes = await clientB.SendAsync(regReq);
                Assert.Equal(HttpStatusCode.NotFound, regRes.StatusCode);
            }

            // 4. PATCH /v1/enrollments/{id} із If-Match та перевіркою версії
            using var patchMismatchReq = new HttpRequestMessage(HttpMethod.Patch, $"/v1/enrollments/{enrollmentId}");
            patchMismatchReq.Headers.TryAddWithoutValidation("If-Match", "\"v99\"");
            patchMismatchReq.Content = new StringContent(JsonSerializer.Serialize(new { group_id = group2 }), Encoding.UTF8, "application/json");
            using var patchMismatchRes = await clientA.SendAsync(patchMismatchReq);
            Assert.Equal(HttpStatusCode.Conflict, patchMismatchRes.StatusCode);

            using var patchOkReq = new HttpRequestMessage(HttpMethod.Patch, $"/v1/enrollments/{enrollmentId}");
            patchOkReq.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
            patchOkReq.Content = new StringContent(JsonSerializer.Serialize(new { group_id = group2 }), Encoding.UTF8, "application/json");
            using var patchOkRes = await clientA.SendAsync(patchOkReq);
            Assert.Equal(HttpStatusCode.OK, patchOkRes.StatusCode);
            Assert.Equal("\"v2\"", patchOkRes.Headers.ETag?.ToString());
            using var patchOkDoc = JsonDocument.Parse(await patchOkRes.Content.ReadAsStringAsync());
            Assert.Equal(group2, patchOkDoc.RootElement.GetProperty("group_id").GetString());
            Assert.Equal(2, patchOkDoc.RootElement.GetProperty("version").GetInt32());

            // 5. Заборона зміни owner_id, student_id, status, version у тілі POST і PATCH -> 422
            var forbiddenPayloads = new[]
            {
                $"{{\"group_id\":\"{group3}\",\"student_id\":\"{studentB}\"}}",
                $"{{\"group_id\":\"{group3}\",\"owner_id\":\"{studentB}\"}}",
                $"{{\"group_id\":\"{group3}\",\"status\":\"confirmed\"}}",
                $"{{\"group_id\":\"{group3}\",\"version\":10}}"
            };

            foreach (var badBody in forbiddenPayloads)
            {
                using var postBad = new HttpRequestMessage(HttpMethod.Post, "/v1/enrollments");
                postBad.Headers.Add("Idempotency-Key", $"o32-422-{Guid.NewGuid():N}");
                postBad.Content = new StringContent(badBody, Encoding.UTF8, "application/json");
                Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clientA.SendAsync(postBad)).StatusCode);

                using var patchBad = new HttpRequestMessage(HttpMethod.Patch, $"/v1/enrollments/{enrollmentId}")
                {
                    Content = new StringContent(badBody, Encoding.UTF8, "application/json")
                };
                Assert.Equal(HttpStatusCode.UnprocessableEntity, (await clientA.SendAsync(patchBad)).StatusCode);
            }

            // 6. POST /v1/groups/{id}/close: 401 анонімний, 403 студент (user), 200 працівник деканату (staff)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.PostAsync($"/v1/groups/{group3}/close", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await clientA.PostAsync($"/v1/groups/{group3}/close", null)).StatusCode);

            using var closeOkRes = await staffClient.PostAsync($"/v1/groups/{group3}/close", null);
            Assert.Equal(HttpStatusCode.OK, closeOkRes.StatusCode);
            using var closeDoc = JsonDocument.Parse(await closeOkRes.Content.ReadAsStringAsync());
            Assert.True(closeDoc.RootElement.GetProperty("closed").GetBoolean());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentA, group1, disc1);
            await TestEnvironment.CleanupTestDataAsync(studentA, group2, disc2);
            await TestEnvironment.CleanupTestDataAsync(studentB, group3, disc3);
        }
    }

    [SkippableFact]
    public async Task Web_O33_CorsProtection_ExplicitAllowedOriginOnly_RejectsEvilOrigin()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        using var client = await CreateAuthenticatedWebClientAsync("student-1");

        // 1. Дозволений Origin http://localhost:3000 -> точне повернення того самого Origin (без '*')
        using var allowedPreflight = new HttpRequestMessage(HttpMethod.Options, "/v1/enrollments");
        allowedPreflight.Headers.Add("Origin", "http://localhost:3000");
        allowedPreflight.Headers.Add("Access-Control-Request-Method", "PATCH");
        allowedPreflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var allowedPreResp = await client.SendAsync(allowedPreflight);
        Assert.Equal(HttpStatusCode.NoContent, allowedPreResp.StatusCode);
        Assert.Equal(
            "http://localhost:3000",
            allowedPreResp.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());

        using var allowedGet = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        allowedGet.Headers.Add("Origin", "http://localhost:3000");
        using var allowedGetResp = await client.SendAsync(allowedGet);
        Assert.Equal(HttpStatusCode.OK, allowedGetResp.StatusCode);
        Assert.Equal(
            "http://localhost:3000",
            allowedGetResp.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());

        // 2. Сторонній Origin http://evil.example -> відсутній Access-Control-Allow-Origin і ніколи не '*'
        using var evilPreflight = new HttpRequestMessage(HttpMethod.Options, "/v1/enrollments");
        evilPreflight.Headers.Add("Origin", "http://evil.example");
        evilPreflight.Headers.Add("Access-Control-Request-Method", "POST");
        evilPreflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var evilPreResp = await client.SendAsync(evilPreflight);
        Assert.False(evilPreResp.Headers.Contains("Access-Control-Allow-Origin"));

        using var evilGet = new HttpRequestMessage(HttpMethod.Get, "/v1/disciplines");
        evilGet.Headers.Add("Origin", "http://evil.example");
        using var evilGetResp = await client.SendAsync(evilGet);
        Assert.Equal(HttpStatusCode.OK, evilGetResp.StatusCode);
        Assert.False(evilGetResp.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
