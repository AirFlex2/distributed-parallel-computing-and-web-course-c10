using System.Text.Json;
using System.Text.RegularExpressions;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using Npgsql;

namespace EnrollmentService.Tests;

public static class TestEnvironment
{
    private static readonly Lazy<Task> DatabaseReadyTask = new(InitializeTestDatabaseAsync);

    public static string ConnectionString => ResolveConnectionString();
    public static string ApiAuthToken => ResolveApiToken();

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

    public static void ValidateTestConnectionString(string connStr)
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

        // Перевірка обходу TEST_ALLOW_UNSAFE_DB_OVERRIDE: доступний виключно в явно контрольованому CI-окруженні (CI=true / GITHUB_ACTIONS=true)
        bool hasOverrideFlag = bool.TryParse(Environment.GetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE"), out var allow) && allow;
        bool isCiEnvironment = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("GITLAB_CI"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("TF_BUILD"), "True", StringComparison.OrdinalIgnoreCase);

        if (hasOverrideFlag && !isCiEnvironment)
        {
            throw new InvalidOperationException(
                "[CRITICAL SAFETY ERROR]: Обхід TEST_ALLOW_UNSAFE_DB_OVERRIDE дозволений виключно в автоматизованому CI-окруженні " +
                "(змінні CI=true або GITHUB_ACTIONS=true) і суворо заборонений для звичайної локальної розробки.");
        }

        bool isCiOverride = hasOverrideFlag && isCiEnvironment;

        // 2. Сувора перевірка імені тестової БД:
        // Вимагається точне ім'я 'dpc_c10_test' або встановлений формат 'dpc_c10_test_<суфікс>'
        bool isExactTestDb = string.Equals(db, "dpc_c10_test", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(db, @"^dpc_c10_test(_[a-z0-9_]+)?$");

        if (!isExactTestDb && !isCiOverride)
        {
            throw new InvalidOperationException(
                $"[CRITICAL SAFETY ERROR]: База даних '{builder.Database}' не відповідає строго встановленому формату тестової БД ('dpc_c10_test'). " +
                $"Підключення до '{builder.Database}' (у тому числі до основної БД 'dpc_c10' або довільних баз) суворо заборонено.");
        }

        // 3. Захист хоста: якщо немає явного CI-обходу, хост має бути локальним
        bool isLocalHost = host is "localhost" or "127.0.0.1" or "::1" or "dpc-c10-postgres" or "postgres";
        if (!isLocalHost && !isCiOverride)
        {
            throw new InvalidOperationException(
                $"[CRITICAL SAFETY ERROR]: Хост '{builder.Host}' не є локальним або дозволеним контейнерним хостом. " +
                "Інтеграційні тести дозволено запускати лише локально або у виділеному CI-оточенні.");
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

    public static async Task EnsureDatabaseCreatedAsync(string? connectionString = null)
    {
        string connStr = connectionString ?? ConnectionString;

        // 1. Обов'язкова валідація connection string до будь-яких операцій з PostgreSQL
        ValidateTestConnectionString(connStr);

        var builder = new NpgsqlConnectionStringBuilder(connStr);
        string targetDb = (builder.Database ?? "dpc_c10_test").Trim();
        string ownerUser = (builder.Username ?? "dpc_user").Trim();

        // 2. Сувора перевірка імені тестової БД: дозволено створювати та перевіряти виключно dpc_c10_test або dpc_c10_test_*
        bool isValidTestDbName = string.Equals(targetDb, "dpc_c10_test", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(targetDb, @"^dpc_c10_test_[a-z0-9_]+$", RegexOptions.IgnoreCase);

        if (!isValidTestDbName)
        {
            throw new InvalidOperationException(
                $"[CRITICAL SAFETY ERROR]: Метод EnsureDatabaseCreatedAsync дозволяє створювати та перевіряти виключно тестові бази даних " +
                $"формату 'dpc_c10_test' або 'dpc_c10_test_*'. Створення та зміна бази даних '{targetDb}' суворо заборонені!");
        }

        if (!Regex.IsMatch(targetDb, @"^[a-zA-Z0-9_]+$"))
        {
            throw new ArgumentException($"Некоректне ім'я цільової тестової бази даних: '{targetDb}'. Дозволені лише символи [a-zA-Z0-9_].");
        }
        if (!Regex.IsMatch(ownerUser, @"^[a-zA-Z0-9_]+$"))
        {
            throw new ArgumentException($"Некоректне ім'я користувача бази даних: '{ownerUser}'. Дозволені лише символи [a-zA-Z0-9_].");
        }

        string[] candidateMaintenanceDbs = { "postgres", "template1" };
        var maintenanceErrors = new List<Exception>();
        bool databaseVerifiedOrCreated = false;

        foreach (var mDb in candidateMaintenanceDbs)
        {
            try
            {
                var mBuilder = new NpgsqlConnectionStringBuilder(connStr)
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
                    string createSql = $"CREATE DATABASE \"{targetDb}\" OWNER \"{ownerUser}\";";
                    try
                    {
                        await using var createCmd = new NpgsqlCommand(createSql, conn);
                        await createCmd.ExecuteNonQueryAsync();
                    }
                    catch (PostgresException pgEx) when (pgEx.SqlState == "42501") // insufficient_privilege
                    {
                        throw new InvalidOperationException(
                            $"[DATABASE PERMISSION ERROR]: Користувач '{ownerUser}' не має дозволу (CREATEDB) на створення тестової бази даних '{targetDb}' через службову БД '{mDb}': {pgEx.Message} (SQLSTATE: {pgEx.SqlState})", pgEx);
                    }
                }

                databaseVerifiedOrCreated = true;
                return;
            }
            catch (InvalidOperationException)
            {
                // Помилки відсутності прав прокидаємо негайно
                throw;
            }
            catch (Exception ex)
            {
                maintenanceErrors.Add(new InvalidOperationException($"Спроба через службову БД '{mDb}' зазнала невдачі: {ex.Message}", ex));
            }
        }

        if (!databaseVerifiedOrCreated)
        {
            string details = string.Join(" | ", maintenanceErrors.Select(e => e.Message));
            Console.Error.WriteLine($"[TestEnvironment Error]: Не вдалося перевірити або створити тестову БД '{targetDb}'. Деталі: {details}");
            throw new InvalidOperationException(
                $"Не вдалося перевірити або створити тестову БД '{targetDb}' через жодну зі службових баз ({string.Join(", ", candidateMaintenanceDbs)}). Деталі: {details}",
                new AggregateException(maintenanceErrors));
        }
    }

    public static async Task DropDatabaseIfExistsAsync(string databaseName)
    {
        if (!Regex.IsMatch(databaseName, @"^dpc_c10_test_[a-z0-9_]+$", RegexOptions.IgnoreCase))
        {
            throw new ArgumentException($"Дозволено видаляти лише тимчасові тестові БД виду 'dpc_c10_test_*': {databaseName}");
        }

        var mBuilder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };
        try
        {
            await using var conn = new NpgsqlConnection(mBuilder.ConnectionString);
            await conn.OpenAsync();

            await using var terminateCmd = new NpgsqlCommand(@"
                SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();", conn);
            terminateCmd.Parameters.AddWithValue("db", databaseName);
            await terminateCmd.ExecuteNonQueryAsync();

            await using var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\";", conn);
            await dropCmd.ExecuteNonQueryAsync();

            // Перевіряємо, що база дійсно більше не існує в pg_database
            await using var verifyCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db;", conn);
            verifyCmd.Parameters.AddWithValue("db", databaseName);
            var exists = await verifyCmd.ExecuteScalarAsync();
            if (exists != null)
            {
                throw new InvalidOperationException($"[CRITICAL SAFETY ERROR]: Тимчасова тестова база даних '{databaseName}' не була видалена після виконання DROP DATABASE.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TestEnvironment Error]: Помилка під час видалення тимчасової тестової БД '{databaseName}': {ex}");
            throw new InvalidOperationException($"Не вдалося видалити тимчасову тестову базу даних '{databaseName}': {ex.Message}", ex);
        }
    }

    public static async Task<bool> IsDatabaseAvailableAsync()
    {
        // 1. Валідація connection string (викине InvalidOperationException при порушенні формату dpc_c10_test)
        string connStr = ConnectionString;

        try
        {
            // 2. СПОЧАТКУ гарантуємо створення тестової БД dpc_c10_test та застосування міграцій
            await EnsureTestDatabaseReadyAsync();

            // 3. ПОТІМ відкриваємо з'єднання та перевіряємо доступність
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();

            await using var pingCmd = new NpgsqlCommand("SELECT 1;", conn);
            await pingCmd.ExecuteScalarAsync();

            return true;
        }
        catch (InvalidOperationException)
        {
            // Помилки валідації безпеки та відсутності прав на створення БД НЕ повинні глушитися
            // і повертати false (що призвело б до пропуску тесту через Skip.If) — вони мають явно завершувати тест помилкою!
            throw;
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
