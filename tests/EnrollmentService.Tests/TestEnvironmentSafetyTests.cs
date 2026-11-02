using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed class TestEnvironmentSafetyTests
{
    private static Task<bool> IsDatabaseAvailableAsync() => TestEnvironment.IsDatabaseAvailableAsync();

    // 1. Тестова БД відсутня, але успішно створюється
    [SkippableFact]
    public async Task TestDatabaseAbsent_IsCreatedSuccessfully()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string tempDb = "dpc_c10_test_tmp_absent";
        var builder = new NpgsqlConnectionStringBuilder(TestEnvironment.ConnectionString)
        {
            Database = tempDb
        };

        await TestEnvironment.DropDatabaseIfExistsAsync(tempDb);
        try
        {
            // Перевіряємо, що БД дійсно відсутня
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var checkCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db;", conn);
                checkCmd.Parameters.AddWithValue("db", tempDb);
                var before = await checkCmd.ExecuteScalarAsync();
                Assert.Null(before);
            }

            // Викликаємо створення
            await TestEnvironment.EnsureDatabaseCreatedAsync(builder.ConnectionString);

            // Перевіряємо, що БД успішно створилася
            await using (var conn = await TestEnvironment.OpenConnectionAsync())
            {
                await using var checkCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db;", conn);
                checkCmd.Parameters.AddWithValue("db", tempDb);
                var after = await checkCmd.ExecuteScalarAsync();
                Assert.NotNull(after);
            }
        }
        finally
        {
            await TestEnvironment.DropDatabaseIfExistsAsync(tempDb);
        }
    }

    // 2. Тестова БД вже існує (ідемпотентність створення)
    [SkippableFact]
    public async Task TestDatabaseAlreadyExists_SucceedsIdempotently()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        // Викликаємо EnsureDatabaseCreatedAsync для вже існуючої dpc_c10_test
        var ex = await Record.ExceptionAsync(() => TestEnvironment.EnsureDatabaseCreatedAsync());
        Assert.Null(ex);
    }

    // 3. Підключення до основної БД dpc_c10 через ValidateTestConnectionString суворо відхиляється
    [Fact]
    public void ValidateTestConnectionString_TargetingMainAppDatabase_ThrowsCriticalSafetyError()
    {
        string mainAppConnStr = "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestEnvironment.ValidateTestConnectionString(mainAppConnStr));

        Assert.Contains("CRITICAL SAFETY ERROR", ex.Message);
        Assert.Contains("dpc_c10_test", ex.Message);
        Assert.Contains("dpc_c10", ex.Message);
    }

    // 4. Підключення до бази з довільною назвою, що містить 'test', через ValidateTestConnectionString відхиляється
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=arbitrary_test_db;Username=dpc_user;Password=dpc_password")]
    [InlineData("Host=localhost;Port=5432;Database=my_test_application;Username=dpc_user;Password=dpc_password")]
    [InlineData("Host=localhost;Port=5432;Database=test;Username=dpc_user;Password=dpc_password")]
    [InlineData("Host=localhost;Port=5432;Database=dpc_test;Username=dpc_user;Password=dpc_password")]
    [InlineData("Host=localhost;Port=5432;Database=production_test_db;Username=dpc_user;Password=dpc_password")]
    public void ValidateTestConnectionString_TargetingArbitraryDatabaseWithTestName_ThrowsCriticalSafetyError(string connStr)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestEnvironment.ValidateTestConnectionString(connStr));

        Assert.Contains("CRITICAL SAFETY ERROR", ex.Message);
    }

    // 5a. Недоступний порт/хост при створенні БД викликає явну помилку, а не пропуск тесту
    [Fact]
    public async Task EnsureDatabaseCreated_WhenHostOrPortUnreachable_ThrowsExplicitException()
    {
        // Неіснуючий порт/хост зі збоєм підключення до службових баз
        string invalidConnStr = "Host=localhost;Port=5433;Database=dpc_c10_test;Username=invalid_user;Password=invalid_pass;Timeout=1";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.EnsureDatabaseCreatedAsync(invalidConnStr));

        Assert.NotNull(ex);
        Assert.Contains("Не вдалося перевірити або створити тестову БД", ex.Message);
    }

    // 5b. PostgreSQL доступний, але користувач не має прав CREATEDB (SQLSTATE 42501)
    [SkippableFact]
    public async Task EnsureDatabaseCreated_WhenUserLacksCreatedbPermission_ThrowsExplicit42501Exception()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string unprivilegedUser = "dpc_nopriv_test_user";
        string unprivilegedPass = "test_nopriv_password";
        string targetDb = "dpc_c10_test_nopriv_probe";

        // 1. Створюємо тимчасового користувача без прав CREATEDB під суперкористувачем
        var adminBuilder = new NpgsqlConnectionStringBuilder(TestEnvironment.ConnectionString)
        {
            Database = "postgres"
        };

        await using (var adminConn = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await adminConn.OpenAsync();

            // Очищення на випадок попереднього аварійного запуску
            await using (var cleanupCmd = new NpgsqlCommand($@"
                REVOKE ALL PRIVILEGES ON DATABASE postgres FROM {unprivilegedUser};
                DROP ROLE IF EXISTS {unprivilegedUser};", adminConn))
            {
                try { await cleanupCmd.ExecuteNonQueryAsync(); } catch { }
            }

            await using var createRoleCmd = new NpgsqlCommand($@"
                CREATE ROLE {unprivilegedUser} WITH LOGIN PASSWORD '{unprivilegedPass}' NOSUPERUSER NOCREATEDB NOCREATEROLE;
                GRANT CONNECT ON DATABASE postgres TO {unprivilegedUser};", adminConn);
            await createRoleCmd.ExecuteNonQueryAsync();
        }

        try
        {
            var unprivilegedBuilder = new NpgsqlConnectionStringBuilder(TestEnvironment.ConnectionString)
            {
                Database = targetDb,
                Username = unprivilegedUser,
                Password = unprivilegedPass,
                Timeout = 5
            };

            // 2. Спроба створити тестову БД через користувача без прав CREATEDB
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TestEnvironment.EnsureDatabaseCreatedAsync(unprivilegedBuilder.ConnectionString));

            // 3. Перевіряємо зрозуміле повідомлення та точний SQLSTATE 42501
            Assert.Contains("DATABASE PERMISSION ERROR", ex.Message);
            Assert.Contains("CREATEDB", ex.Message);

            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("42501", pgEx.SqlState);
        }
        finally
        {
            // 4. Гарантоване видалення створеного тестового користувача та можливої тестової БД
            await using (var adminConn = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await adminConn.OpenAsync();
                await using var dropRoleCmd = new NpgsqlCommand($@"
                    REVOKE ALL PRIVILEGES ON DATABASE postgres FROM {unprivilegedUser};
                    DROP ROLE IF EXISTS {unprivilegedUser};", adminConn);
                try { await dropRoleCmd.ExecuteNonQueryAsync(); } catch { }
            }

            try
            {
                await TestEnvironment.DropDatabaseIfExistsAsync(targetDb);
            }
            catch { }
        }
    }

    // 6. IsDatabaseAvailableAsync() не пропускає етап створення БД і не глушить помилки безпеки
    //    Зберігає та відновлює ВСІ змінні середовища CI (CI, GITHUB_ACTIONS, GITLAB_CI, TF_BUILD)
    [Fact]
    public async Task IsDatabaseAvailableAsync_DoesNotSkipCreationPhaseAndPropagatesSafetyErrors()
    {
        // Зберігаємо оригінальні значення ВСІХ змінних оточення CI та налаштувань безпеки
        string? originalOverride = Environment.GetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE");
        string? originalDb = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION_STRING");
        string? originalCi = Environment.GetEnvironmentVariable("CI");
        string? originalGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        string? originalGitlabCi = Environment.GetEnvironmentVariable("GITLAB_CI");
        string? originalTfBuild = Environment.GetEnvironmentVariable("TF_BUILD");

        try
        {
            // Симулюємо звичайне локальне середовище без жодних CI-маркерів
            Environment.SetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE", "true");
            Environment.SetEnvironmentVariable("CI", null);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
            Environment.SetEnvironmentVariable("GITLAB_CI", null);
            Environment.SetEnvironmentVariable("TF_BUILD", null);

            // IsDatabaseAvailableAsync() зобов'язаний негайно викинути InvalidOperationException,
            // а не проковтнути помилку через catch і повернути false (що спричинило б пропуск тесту через Skip.If)
            await Assert.ThrowsAsync<InvalidOperationException>(() => TestEnvironment.IsDatabaseAvailableAsync());
        }
        finally
        {
            // Гарантоване відновлення ВСІХ змінних, навіть якщо тест запущено безпосередньо в GitHub Actions / GitLab CI
            Environment.SetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE", originalOverride);
            Environment.SetEnvironmentVariable("TEST_DB_CONNECTION_STRING", originalDb);
            Environment.SetEnvironmentVariable("CI", originalCi);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGithubActions);
            Environment.SetEnvironmentVariable("GITLAB_CI", originalGitlabCi);
            Environment.SetEnvironmentVariable("TF_BUILD", originalTfBuild);
        }
    }

    // 7. Прямий виклик EnsureDatabaseCreatedAsync() з основною БД dpc_c10 суворо блокується
    [Fact]
    public async Task EnsureDatabaseCreatedAsync_TargetingMainAppDatabase_ThrowsCriticalSafetyError()
    {
        string mainDbConnStr = "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.EnsureDatabaseCreatedAsync(mainDbConnStr));

        Assert.Contains("CRITICAL SAFETY ERROR", ex.Message);
        Assert.Contains("dpc_c10", ex.Message);
    }

    // 8. Прямий виклик EnsureDatabaseCreatedAsync() з довільним ім'ям бази (у т.ч. що містить 'test') блокується
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=arbitrary_db;Username=dpc_user;Password=dpc_password")]
    [InlineData("Host=localhost;Port=5432;Database=custom_test_catalog;Username=dpc_user;Password=dpc_password")]
    [InlineData("Host=localhost;Port=5432;Database=production;Username=dpc_user;Password=dpc_password")]
    [InlineData("Host=localhost;Port=5432;Database=dpc_test;Username=dpc_user;Password=dpc_password")]
    public async Task EnsureDatabaseCreatedAsync_TargetingArbitraryDatabaseName_ThrowsCriticalSafetyError(string connStr)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.EnsureDatabaseCreatedAsync(connStr));

        Assert.Contains("CRITICAL SAFETY ERROR", ex.Message);
    }

    // 9. Прямий виклик EnsureDatabaseCreatedAsync() з віддаленим хостом суворо блокується
    [Fact]
    public async Task EnsureDatabaseCreatedAsync_TargetingRemoteHost_ThrowsCriticalSafetyError()
    {
        string remoteConnStr = "Host=192.168.1.100;Port=5432;Database=dpc_c10_test;Username=dpc_user;Password=dpc_password";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.EnsureDatabaseCreatedAsync(remoteConnStr));

        Assert.Contains("CRITICAL SAFETY ERROR", ex.Message);
        Assert.Contains("192.168.1.100", ex.Message);
    }
}
