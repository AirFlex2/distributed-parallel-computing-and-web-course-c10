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
    public async Task CancelEnrollment_Held_Success_Returns200AndFreesSeats()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-cancel-held";
        string groupId = "GRP-CANCEL-HELD";
        string discId = "DISC-CANCEL-HELD";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"cancel-held-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            // Перевіряємо списання місця у Web
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmdSeats = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @id;", conn);
                cmdSeats.Parameters.AddWithValue("id", groupId);
                int avail = (int)(await cmdSeats.ExecuteScalarAsync())!;
                Assert.Equal(4, avail);
            }

            // Скасування запису held
            var cancelResp = await client.PostAsync($"/v1/enrollments/{enrollmentId}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
            var cancelJson = await cancelResp.Content.ReadAsStringAsync();
            using var cancelDoc = JsonDocument.Parse(cancelJson);
            Assert.Equal("cancelled", cancelDoc.RootElement.GetProperty("status").GetString());

            // Місце повернуто у групу, лічильники студента зменшено
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmdSeats = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @id;", conn);
                cmdSeats.Parameters.AddWithValue("id", groupId);
                int avail = (int)(await cmdSeats.ExecuteScalarAsync())!;
                Assert.Equal(5, avail);

                await using var cmdStud = new NpgsqlCommand("SELECT active_count, active_ects FROM student WHERE id = @id;", conn);
                cmdStud.Parameters.AddWithValue("id", studentId);
                await using var rdr = await cmdStud.ExecuteReaderAsync();
                Assert.True(await rdr.ReadAsync());
                Assert.Equal(0, rdr.GetInt32(0));
                Assert.Equal(0.0m, rdr.GetDecimal(1));
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task CancelEnrollment_Confirmed_Returns409Conflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-cancel-conf";
        string groupId = "GRP-CANCEL-CONF";
        string discId = "DISC-CANCEL-CONF";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"cancel-conf-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            // Переводимо у confirmed
            var confResp = await client.PostAsync($"/v1/enrollments/{enrollmentId}/confirm", null);
            Assert.Equal(HttpStatusCode.OK, confResp.StatusCode);

            // Спроба скасувати підтверджений запис -> 409 Conflict
            var cancelResp = await client.PostAsync($"/v1/enrollments/{enrollmentId}/cancel", null);
            Assert.Equal(HttpStatusCode.Conflict, cancelResp.StatusCode);

            var cancelJson = await cancelResp.Content.ReadAsStringAsync();
            using var cancelDoc = JsonDocument.Parse(cancelJson);
            Assert.Equal("enrollment_already_confirmed", cancelDoc.RootElement.GetProperty("error_code").GetString());

            // Перевіряємо, що запис залишився confirmed
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("SELECT status FROM enrollment WHERE id = @id;", conn);
                cmd.Parameters.AddWithValue("id", enrollmentId);
                string status = (string)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal("confirmed", status);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task CancelEnrollment_Expired_Returns409Conflict()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-cancel-exp";
        string groupId = "GRP-CANCEL-EXP";
        string discId = "DISC-CANCEL-EXP";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"cancel-exp-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            // Симулюємо сплив часу резервування (expires_at у минулому, з дотриманням expires_at > created_at)
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("UPDATE enrollment SET created_at = NOW() - INTERVAL '20 minutes', expires_at = NOW() - INTERVAL '5 minutes' WHERE id = @id;", conn);
                cmd.Parameters.AddWithValue("id", enrollmentId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Спроба скасувати прострочений запис -> 409 Conflict
            var cancelResp = await client.PostAsync($"/v1/enrollments/{enrollmentId}/cancel", null);
            Assert.Equal(HttpStatusCode.Conflict, cancelResp.StatusCode);

            var cancelJson = await cancelResp.Content.ReadAsStringAsync();
            using var cancelDoc = JsonDocument.Parse(cancelJson);
            Assert.Equal("enrollment_expired", cancelDoc.RootElement.GetProperty("error_code").GetString());
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task CancelEnrollment_Cancelled_Returns200Ok_DoesNotFreeSeatsTwice()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-cancel-idem";
        string groupId = "GRP-CANCEL-IDEM";
        string discId = "DISC-CANCEL-IDEM";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"cancel-idem-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);

            // Перше скасування -> 200 OK
            var cancelResp1 = await client.PostAsync($"/v1/enrollments/{enrollmentId}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancelResp1.StatusCode);

            // Повторне скасування (ідемпотентне за Д2.5) -> 200 OK
            var cancelResp2 = await client.PostAsync($"/v1/enrollments/{enrollmentId}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancelResp2.StatusCode);

            // Перевіряємо, що місце не звільнено вдруге (available має дорівнювати 5, а не 6)
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmdSeats = new NpgsqlCommand("SELECT available FROM discipline_group WHERE id = @id;", conn);
                cmdSeats.Parameters.AddWithValue("id", groupId);
                int avail = (int)(await cmdSeats.ExecuteScalarAsync())!;
                Assert.Equal(5, avail);
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }

    [SkippableFact]
    public async Task GetStudents_WithoutAuth_Returns401Unauthorized()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/v1/students");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(401, doc.RootElement.GetProperty("status").GetInt32());
    }

    [SkippableFact]
    public async Task GetStudents_WithBearerToken_ReturnsFullList()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

        var resp = await client.GetAsync("/v1/students");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 1);
    }

    [SkippableFact]
    public async Task GetStudents_WithStudentXUserId_ReturnsOnlyOwnData()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");

        var resp = await client.GetAsync("/v1/students");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("student-1", items[0].GetProperty("id").GetString());
    }

    [SkippableFact]
    public async Task GetStudents_WithStudentXUserId_AccessingOtherStudent_Returns403Forbidden()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");

        var resp = await client.GetAsync("/v1/students?student_id=student-2");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(403, doc.RootElement.GetProperty("status").GetInt32());
    }

    [SkippableFact]
    public async Task GetStudentById_WithoutAuth_Returns401Unauthorized()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/v1/students/student-1");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task GetStudentById_ByStudent_OwnData_Returns200Ok()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");

        var resp = await client.GetAsync("/v1/students/student-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("student-1", doc.RootElement.GetProperty("id").GetString());
    }

    [SkippableFact]
    public async Task GetStudentById_ByStudent_OtherData_Returns403Forbidden()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "student-1");

        var resp = await client.GetAsync("/v1/students/student-2");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task GetStudentById_ByStaff_Returns200Ok()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);

        var resp = await client.GetAsync("/v1/students/student-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("student-1", doc.RootElement.GetProperty("id").GetString());
    }

    [SkippableFact]
    public async Task GetEnrollment_RepeatedCalls_DoNotMutateStateOrVersion()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string studentId = "student-get-safe";
        string groupId = "GRP-GET-SAFE";
        string discId = "DISC-GET-SAFE";

        await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);

        try
        {
            await EnsureTestStudentAsync(studentId, maxDisciplines: 5, activeCount: 0, activeEcts: 0.0m);
            await EnsureTestGroupAsync(groupId, discId, seats: 5, available: 5, opensAt: DateTimeOffset.UtcNow.AddDays(-1), closesAt: DateTimeOffset.UtcNow.AddDays(1), ects: 4.0m);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", studentId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"ik-get-safe-{Guid.NewGuid():N}");

            var postResp = await client.PostAsync("/v1/enrollments", new StringContent($"{{\"group_id\": \"{groupId}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            var postJson = await postResp.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postJson);
            Guid enrollmentId = Guid.Parse(postDoc.RootElement.GetProperty("id").GetString()!);
            int initialVersion = postDoc.RootElement.GetProperty("version").GetInt32();
            string initialStatus = postDoc.RootElement.GetProperty("status").GetString()!;

            Assert.Equal("held", initialStatus);
            Assert.Equal(1, initialVersion);

            // Виконуємо 5 послідовних GET-запитів
            for (int i = 0; i < 5; i++)
            {
                var getResp = await client.GetAsync($"/v1/enrollments/{enrollmentId}");
                Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
                var getJson = await getResp.Content.ReadAsStringAsync();
                using var getDoc = JsonDocument.Parse(getJson);
                Assert.Equal("held", getDoc.RootElement.GetProperty("status").GetString());
                Assert.Equal(initialVersion, getDoc.RootElement.GetProperty("version").GetInt32());
            }

            // Перевіряємо в БД, що стан і версія не змінилися (RFC 9110 Safe Method)
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand("SELECT status, version FROM enrollment WHERE id = @id;", conn);
                cmd.Parameters.AddWithValue("id", enrollmentId);
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("held", reader.GetString(0));
                Assert.Equal(initialVersion, reader.GetInt32(1));
            }
        }
        finally
        {
            await TestEnvironment.CleanupTestDataAsync(studentId, groupId, discId);
        }
    }
}
