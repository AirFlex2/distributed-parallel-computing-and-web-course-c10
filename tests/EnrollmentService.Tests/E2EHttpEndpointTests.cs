using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnrollmentService.Queues;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
        decimal ects = 3.0m,
        bool dpcSandboxEnabled = true)
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
            INSERT INTO discipline_group (id, discipline_id, group_code, seats, available, opens_at, closes_at, university_tz, closed, dpc_sandbox_enabled)
            VALUES (@groupId, @discId, 'GRP-' || @groupId, @seats, @available, @opensAt, @closesAt, 'Europe/Kyiv', FALSE, @dpcSandboxEnabled)
            ON CONFLICT (id) DO UPDATE SET seats = @seats, available = @available, opens_at = @opensAt, closes_at = @closesAt, closed = FALSE, dpc_sandbox_enabled = @dpcSandboxEnabled;", conn);
        cmdGroup.Parameters.AddWithValue("groupId", groupId);
        cmdGroup.Parameters.AddWithValue("discId", disciplineId);
        cmdGroup.Parameters.AddWithValue("seats", seats);
        cmdGroup.Parameters.AddWithValue("available", available);
        cmdGroup.Parameters.AddWithValue("opensAt", opensAt);
        cmdGroup.Parameters.AddWithValue("closesAt", closesAt);
        cmdGroup.Parameters.AddWithValue("dpcSandboxEnabled", dpcSandboxEnabled);
        await cmdGroup.ExecuteNonQueryAsync();
    }

    private static async Task WaitForTerminalStatusAsync(
        HttpClient client,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        while (true)
        {
            var response = await client.GetAsync($"/v1/enrollment-requests/{requestId}", linked.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(linked.Token);
            using var doc = JsonDocument.Parse(json);
            string status = doc.RootElement.GetProperty("status").GetString()!;

            if (status is "done" or "rejected" or "failed")
            {
                return;
            }

            await Task.Delay(50, linked.Token);
        }
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

            // Фіксуємо початковий стан групи DISC-WEB-01
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
                await using var reader = await cmdStud.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt32(0));
                Assert.Equal(0.0m, reader.GetDecimal(1));

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
}
