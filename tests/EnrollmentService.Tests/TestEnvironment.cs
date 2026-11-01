using System.Text.Json;
using EnrollmentService.Repositories;
using Npgsql;

namespace EnrollmentService.Tests;

public static class TestEnvironment
{
    private static readonly Lazy<string> CachedConnectionString = new(ResolveConnectionString);
    private static readonly Lazy<string> CachedApiToken = new(ResolveApiToken);

    public static string ConnectionString => CachedConnectionString.Value;
    public static string ApiAuthToken => CachedApiToken.Value;

    private static string ResolveConnectionString()
    {
        // 1. Пріоритет: явна змінна середовища для тестової БД
        string? envTest = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envTest))
        {
            return envTest.Trim();
        }

        // 2. Змінна середовища загальної БД
        string? envDb = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envDb))
        {
            return envDb.Trim();
        }

        // 3. Файл appsettings.Test.json поруч із тестовою збіркою
        try
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Test.json");
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("TEST_DB_CONNECTION_STRING", out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    string? val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                }
            }
        }
        catch { }

        // 4. Локальний дефолт (розробницький контейнер postgres)
        return "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password;Maximum Pool Size=50";
    }

    private static string ResolveApiToken()
    {
        string? envToken = Environment.GetEnvironmentVariable("TEST_API_AUTH_TOKEN")
            ?? Environment.GetEnvironmentVariable("API_AUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return envToken.Trim();
        }

        return "secret-c10-token-2026";
    }

    public static async Task<bool> IsDatabaseAvailableAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    public static IDbConnectionFactory CreateDbConnectionFactory()
    {
        return new TestDbConnectionFactory();
    }

    private sealed class TestDbConnectionFactory : IDbConnectionFactory
    {
        public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            return TestEnvironment.OpenConnectionAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Гарантоване та безпечне очищення створених під час тестів сутностей (студенти, групи, дисципліни, заявки, записи).
    /// </summary>
    public static async Task CleanupTestDataAsync(
        string? studentId = null,
        string? groupId = null,
        string? disciplineId = null,
        Guid? requestId = null)
    {
        try
        {
            await using var conn = await OpenConnectionAsync();

            if (requestId.HasValue)
            {
                await using var cmdReq = new NpgsqlCommand(@"
                    DELETE FROM decanate_roster WHERE request_id = @reqId;
                    DELETE FROM enrollment WHERE request_id = @reqId;
                    DELETE FROM enrollment_requests WHERE id = @reqId;", conn);
                cmdReq.Parameters.AddWithValue("reqId", requestId.Value);
                await cmdReq.ExecuteNonQueryAsync();
            }

            if (!string.IsNullOrWhiteSpace(studentId))
            {
                await using var cmdStud = new NpgsqlCommand(@"
                    DELETE FROM decanate_roster WHERE student_id = @id;
                    DELETE FROM enrollment WHERE student_id = @id;
                    DELETE FROM enrollment_requests WHERE student_id = @id;
                    DELETE FROM student WHERE id = @id AND id LIKE 'student-e2e-%';", conn);
                cmdStud.Parameters.AddWithValue("id", studentId);
                await cmdStud.ExecuteNonQueryAsync();
            }

            if (!string.IsNullOrWhiteSpace(groupId))
            {
                await using var cmdGrp = new NpgsqlCommand(@"
                    DELETE FROM decanate_roster WHERE group_id = @grpId;
                    DELETE FROM enrollment WHERE group_id = @grpId;
                    DELETE FROM enrollment_requests WHERE group_id = @grpId;
                    DELETE FROM discipline_group WHERE id = @grpId AND id LIKE 'GRP-E2E-%';", conn);
                cmdGrp.Parameters.AddWithValue("grpId", groupId);
                await cmdGrp.ExecuteNonQueryAsync();
            }

            if (!string.IsNullOrWhiteSpace(disciplineId))
            {
                await using var cmdDisc = new NpgsqlCommand(@"
                    DELETE FROM discipline WHERE id = @discId AND id LIKE 'DISC-E2E-%';", conn);
                cmdDisc.Parameters.AddWithValue("discId", disciplineId);
                await cmdDisc.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            // Очищення не повинно кидати виняток, що перериває звітування тесту
        }
    }
}
