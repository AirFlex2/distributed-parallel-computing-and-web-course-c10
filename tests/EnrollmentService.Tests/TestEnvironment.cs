using System.Text.Json;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using Npgsql;

namespace EnrollmentService.Tests;

public static class TestEnvironment
{
    private static readonly Lazy<string> CachedConnectionString = new(ResolveConnectionString);
    private static readonly Lazy<string> CachedApiToken = new(ResolveApiToken);
    private static readonly Lazy<Task> DatabaseReadyTask = new(InitializeTestDatabaseAsync);

    public static string ConnectionString => CachedConnectionString.Value;
    public static string ApiAuthToken => CachedApiToken.Value;

    public static Task EnsureTestDatabaseReadyAsync() => DatabaseReadyTask.Value;

    private static string ResolveConnectionString()
    {
        string candidate = ResolveRawConnectionString();
        ValidateTestConnectionString(candidate);
        return candidate;
    }

    private static string ResolveRawConnectionString()
    {
        // 1. Пріоритет: явна змінна середовища виключно для тестової БД
        string? envTest = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envTest))
        {
            return envTest.Trim();
        }

        // 2. Файл appsettings.Test.json поруч із тестовою збіркою
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

        // 3. Безпечний локальний дефолт: суворо виділена тестова БД dpc_c10_test
        return "Host=localhost;Port=5432;Database=dpc_c10_test;Username=dpc_user;Password=dpc_password;Maximum Pool Size=50";
    }

    private static void ValidateTestConnectionString(string connStr)
    {
        var builder = new NpgsqlConnectionStringBuilder(connStr);
        string host = (builder.Host ?? "").Trim().ToLowerInvariant();
        string db = (builder.Database ?? "").Trim().ToLowerInvariant();

        // 1. Захист від виробничих баз за ключовими словами
        if (db.Contains("prod") || db.Contains("live") || db.Contains("production"))
        {
            throw new InvalidOperationException(
                $"[CRITICAL SAFETY ERROR]: База даних '{builder.Database}' на хості '{builder.Host}' містить ознаки робочого середовища (prod/live/production). " +
                "Запуск інтеграційних тестів із модифікацією та очищенням даних на цій БД суворо заборонено!");
        }

        // Перевірка дозволу примусового обходу виключно для спеціалізованого CI-сценарію
        bool isCiOverride = bool.TryParse(Environment.GetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE"), out var allow) && allow;

        // 2. Тестове підключення суворо вимагає явного маркера тестової БД ('test' у назві, наприклад 'dpc_c10_test'),
        // а не просто локального хоста. Локальний хост сам по собі не захищає від перезапису основної БД dpc_c10.
        bool isExplicitTestDb = db.Contains("test");
        if (!isExplicitTestDb && !isCiOverride)
        {
            throw new InvalidOperationException(
                $"[CRITICAL SAFETY ERROR]: База даних '{builder.Database}' не має обов'язкового маркера тестової БД ('test' у назві, наприклад 'dpc_c10_test'). " +
                $"Сам лише локальний хост '{builder.Host}' не доводить, що база є тестовою. " +
                "Інтеграційні та E2E-тести вимагають окремої виділеної тестової бази даних для запобігання пошкодженню основної бази розробки або експлуатації.");
        }
    }

    private static string ResolveApiToken()
    {
        // 1. Пріоритет: явна змінна середовища виключно для тестового токена
        string? envToken = Environment.GetEnvironmentVariable("TEST_API_AUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return envToken.Trim();
        }

        // 2. Файл appsettings.Test.json
        try
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Test.json");
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("TEST_API_AUTH_TOKEN", out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    string? val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                }
            }
        }
        catch { }

        // 3. Виділений тестовий токен за замовчуванням (строго відмінний від робочого секрету)
        return "test-c10-isolated-bearer-token";
    }

    private static async Task InitializeTestDatabaseAsync()
    {
        await EnsureDatabaseCreatedAsync();

        var options = new ServiceOptions
        {
            DbConnectionString = ConnectionString,
            InstanceId = "test-migrator"
        };
        var initializer = new DatabaseInitializer(options);
        await initializer.InitializeAsync();
    }

    private static async Task EnsureDatabaseCreatedAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        string targetDb = builder.Database ?? "dpc_c10_test";
        string ownerUser = builder.Username ?? "dpc_user";

        string[] candidateMaintenanceDbs = { "postgres", "dpc_c10" };

        foreach (var mDb in candidateMaintenanceDbs)
        {
            try
            {
                var mBuilder = new NpgsqlConnectionStringBuilder(ConnectionString)
                {
                    Database = mDb
                };

                await using var conn = new NpgsqlConnection(mBuilder.ConnectionString);
                await conn.OpenAsync();

                await using var checkCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db;", conn);
                checkCmd.Parameters.AddWithValue("db", targetDb);
                var exists = await checkCmd.ExecuteScalarAsync();

                if (exists == null)
                {
                    await using var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{targetDb}\" OWNER \"{ownerUser}\";", conn);
                    await createCmd.ExecuteNonQueryAsync();
                }

                return;
            }
            catch
            {
                // Пробуємо наступну системну БД або продовжуємо, якщо створення неможливе
            }
        }
    }

    public static async Task<bool> IsDatabaseAvailableAsync()
    {
        string connStr = ConnectionString;

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await EnsureTestDatabaseReadyAsync();
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
        catch (System.IO.IOException)
        {
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public static async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTestDatabaseReadyAsync();
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestEnvironment Cleanup Error]: Помилка обов'язкового очищення тестових даних (student='{studentId}', group='{groupId}', request='{requestId}'): {ex}");
            throw new InvalidOperationException($"Mandatory test data cleanup failed for student='{studentId}', group='{groupId}': {ex.Message}", ex);
        }
    }
}
