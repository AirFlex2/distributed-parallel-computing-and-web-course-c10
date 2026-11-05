using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnrollmentService;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services.Processing;
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
        client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-syntax-err");

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
        client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-non-existent");

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
            client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-full-group");

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
            content1.Headers.Add("Idempotency-Key", "test-idem-dup-1");
            var response1 = await client.PostAsync("/v1/enrollments", content1);
            Assert.Equal(HttpStatusCode.Created, response1.StatusCode);

            // Другий запис на ту саму дисципліну — конфлікт (409 Conflict)
            var content2 = new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json");
            content2.Headers.Add("Idempotency-Key", "test-idem-dup-2");
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
            client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-ects");

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
            client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-ects");

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
            client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-ects");

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
        client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-owner");

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
            client.DefaultRequestHeaders.Add("Idempotency-Key", "test-idem-ects");

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
    public async Task Web_O22_IdempotencyKey_DifferentPayload_Returns409Conflict()
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

            // Другий запит із тим самим ключем, але іншою групою -> 409 Conflict
            var content2 = new StringContent($"{{\"group_id\": \"{groupId2}\"}}", Encoding.UTF8, "application/json");
            var response2 = await client.PostAsync("/v1/enrollments", content2);
            Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);

            var json2 = await response2.Content.ReadAsStringAsync();
            using var doc2 = JsonDocument.Parse(json2);
            var root2 = doc2.RootElement;
            Assert.Equal("idempotency_key_conflict", root2.GetProperty("error_code").GetString());
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

    private static string FindMigrationFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "migrations", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        var curDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (curDir != null)
        {
            var candidate = Path.Combine(curDir.FullName, "migrations", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            curDir = curDir.Parent;
        }

        throw new FileNotFoundException($"Файл міграції '{fileName}' не знайдено.");
    }

    private static Exception CombineExceptions(Exception? first, Exception second)
    {
        if (first == null)
        {
            return second;
        }

        if (first is AggregateException agg)
        {
            var list = new List<Exception>(agg.InnerExceptions) { second };
            return new AggregateException(list);
        }

        return new AggregateException(first, second);
    }
}
