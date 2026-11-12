using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnrollmentService;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services.Processing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class E2EHttpEndpointTests : IClassFixture<WebApplicationFactory<Program>>
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
        if (client.DefaultRequestHeaders.Authorization == null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);
        }

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
