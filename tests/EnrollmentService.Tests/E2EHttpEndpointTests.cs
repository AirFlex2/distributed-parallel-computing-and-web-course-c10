using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed class E2EHttpEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public E2EHttpEndpointTests(WebApplicationFactory<Program> factory)
    {
        // Налаштовуємо тестовий веб-сервер на використання виділеної тестової конфігурації БД та токена
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DB_CONNECTION_STRING", TestEnvironment.ConnectionString);
            builder.UseSetting("API_AUTH_TOKEN", TestEnvironment.ApiAuthToken);
        });
    }

    private static Task<bool> IsDatabaseAvailableAsync() => TestEnvironment.IsDatabaseAvailableAsync();

    private static async Task EnsureTestStudentAsync(string studentId, int maxDisciplines = 5, int activeCount = 0, decimal activeEcts = 0.0m)
    {
        await using var conn = await TestEnvironment.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO student (id, email, full_name, max_disciplines, active_count, active_ects)
            VALUES (@id, @email, @name, @maxDisc, @activeCount, @activeEcts)
            ON CONFLICT (id) DO UPDATE SET max_disciplines = @maxDisc, active_count = @activeCount, active_ects = @activeEcts;", conn);
        cmd.Parameters.AddWithValue("id", studentId);
        cmd.Parameters.AddWithValue("email", $"{studentId}@test.local");
        cmd.Parameters.AddWithValue("name", $"E2E Test Student {studentId}");
        cmd.Parameters.AddWithValue("maxDisc", maxDisciplines);
        cmd.Parameters.AddWithValue("activeCount", activeCount);
        cmd.Parameters.AddWithValue("activeEcts", activeEcts);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureTestGroupAsync(
        string groupId,
        string disciplineId,
        int seats,
        int available,
        DateTimeOffset opensAt,
        DateTimeOffset closesAt,
        decimal ects = 3.0m)
    {
        await using var conn = await TestEnvironment.OpenConnectionAsync();

        await using var cmdDisc = new NpgsqlCommand(@"
            INSERT INTO discipline (id, code, title, ects, is_test_only)
            VALUES (@discId, 'CODE-' || @discId, 'Test Discipline ' || @discId, @ects, TRUE)
            ON CONFLICT (id) DO UPDATE SET ects = @ects;", conn);
        cmdDisc.Parameters.AddWithValue("discId", disciplineId);
        cmdDisc.Parameters.AddWithValue("ects", ects);
        await cmdDisc.ExecuteNonQueryAsync();

        await using var cmdGroup = new NpgsqlCommand(@"
            INSERT INTO discipline_group (id, discipline_id, group_code, seats, available, opens_at, closes_at, university_tz, closed)
            VALUES (@groupId, @discId, 'GRP-' || @groupId, @seats, @available, @opensAt, @closesAt, 'Europe/Kyiv', FALSE)
            ON CONFLICT (id) DO UPDATE SET seats = @seats, available = @available, opens_at = @opensAt, closes_at = @closesAt, closed = FALSE;", conn);
        cmdGroup.Parameters.AddWithValue("groupId", groupId);
        cmdGroup.Parameters.AddWithValue("discId", disciplineId);
        cmdGroup.Parameters.AddWithValue("seats", seats);
        cmdGroup.Parameters.AddWithValue("available", available);
        cmdGroup.Parameters.AddWithValue("opensAt", opensAt);
        cmdGroup.Parameters.AddWithValue("closesAt", closesAt);
        await cmdGroup.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task GetHealth_ReturnsOkViaTestServer()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task PostEnrollment_MalformedJsonSyntax_Returns400BadRequest()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");

        var content = new StringContent("{ group_id: unquoted_syntax_error }", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/enrollments", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.Contains("invalid_json_syntax", root.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task PostEnrollment_MissingUserIdHeader_Returns400BadRequest()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        var content = new StringContent("{\"group_id\": \"DISC-WEB-01\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/enrollments", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("missing_user_header", doc.RootElement.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task PostEnrollment_NonExistentGroup_Returns404NotFound()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");

        var content = new StringContent("{\"group_id\": \"NON-EXISTENT-GROUP-XYZ\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/enrollments", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Contains("group_not_found", root.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task PostEnrollment_GroupFull_Returns409Conflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-full-test";
        string groupId = "GRP-E2E-FULL-SEATS";
        string discId = "DISC-E2E-FULL";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 5,
                available: 0, // 0 вільних місць
                opensAt: DateTimeOffset.UtcNow.AddDays(-1),
                closesAt: DateTimeOffset.UtcNow.AddDays(1));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);

            var content = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/v1/enrollments", content);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(409, root.GetProperty("status").GetInt32());
            Assert.Contains("group_full", root.GetProperty("type").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task PostEnrollment_DuplicateDisciplineEnrollment_Returns409Conflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-dup-test";
        string groupId = "GRP-E2E-DUP-G1";
        string discId = "DISC-E2E-DUP";

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
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);

            // Перший запис — успішний (201 Created)
            var content1 = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response1 = await client.PostAsync("/v1/enrollments", content1);
            Assert.Equal(HttpStatusCode.Created, response1.StatusCode);

            // Другий запис на ту саму дисципліну — конфлікт (409 Conflict)
            var content2 = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response2 = await client.PostAsync("/v1/enrollments", content2);

            Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);
            Assert.Equal("application/problem+json", response2.Content.Headers.ContentType?.MediaType);

            var json = await response2.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(409, root.GetProperty("status").GetInt32());
            Assert.Contains("duplicate_discipline_enrollment", root.GetProperty("type").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task PostEnrollment_EctsLimitExceeded_Returns422UnprocessableContent()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-ects-test";
        string groupId = "GRP-E2E-ECTS-HIGH";
        string discId = "DISC-E2E-ECTS-HIGH";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            // Студент уже має 25.0 ECTS з 30.0 максимальних
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 3, activeEcts: 25.0m);
            // Дисципліна на 6.0 ECTS -> 25.0 + 6.0 = 31.0 > 30.0 (перевищення ліміту)
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 10,
                available: 10,
                opensAt: DateTimeOffset.UtcNow.AddDays(-1),
                closesAt: DateTimeOffset.UtcNow.AddDays(1),
                ects: 6.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);

            var content = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/v1/enrollments", content);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(422, root.GetProperty("status").GetInt32());
            Assert.Contains("ects_limit_exceeded", root.GetProperty("type").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task PostEnrollment_BeforeOpensAt_Returns422UnprocessableContent()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-before-open-test";
        string groupId = "GRP-E2E-NOT-OPEN-YET";
        string discId = "DISC-E2E-NOT-OPEN-YET";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            // Вікно ще не відкрилося (opens_at у майбутньому)
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 10,
                available: 10,
                opensAt: DateTimeOffset.UtcNow.AddDays(2),
                closesAt: DateTimeOffset.UtcNow.AddDays(5));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);

            var content = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/v1/enrollments", content);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(422, root.GetProperty("status").GetInt32());
            Assert.Contains("enrollment_window_closed", root.GetProperty("type").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task PostEnrollment_OutsideEnrollmentWindow_Returns422UnprocessableContent()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-window-test";
        string groupId = "GRP-E2E-WINDOW-CLOSED";
        string discId = "DISC-E2E-WINDOW-CLOSED";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            // Вікно закрито в минулому
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(
                groupId,
                discId,
                seats: 10,
                available: 10,
                opensAt: DateTimeOffset.UtcNow.AddDays(-10),
                closesAt: DateTimeOffset.UtcNow.AddDays(-5));

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);

            var content = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/v1/enrollments", content);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(422, root.GetProperty("status").GetInt32());
            Assert.Contains("enrollment_window_closed", root.GetProperty("type").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task PostEnrollment_OwnerInBodyForbidden_Returns422UnprocessableContent()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");

        // Заборонено передавати student_id у тілі для захисту від підміни авторства (Web О1.4)
        var content = new StringContent("{\"group_id\": \"DISC-WEB-01\", \"student_id\": \"student-2\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/enrollments", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(422, root.GetProperty("status").GetInt32());
        Assert.Contains("owner_in_body_forbidden", root.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task PostEnrollment_ValidBooking_Returns201CreatedWithLocationHeader()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-e2e-valid-test";
        string groupId = "GRP-E2E-VALID-G1";
        string discId = "DISC-E2E-VALID";

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
                ects: 4.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);

            var content = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/v1/enrollments", content);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(response.Headers.Location);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string enrollmentId = root.GetProperty("id").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(enrollmentId));
            Assert.Equal($"/v1/enrollments/{enrollmentId}", response.Headers.Location.ToString());
            Assert.Equal("held", root.GetProperty("status").GetString());
            Assert.Equal(studentId, root.GetProperty("student_id").GetString());
            Assert.Equal(groupId, root.GetProperty("group_id").GetString());

            // Перевіряємо GET /v1/enrollments/{id}
            var getResponse = await client.GetAsync($"/v1/enrollments/{enrollmentId}");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

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
}
