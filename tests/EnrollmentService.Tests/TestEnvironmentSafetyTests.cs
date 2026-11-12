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

    // 5a. Недоступний порт/хост при виклику EnsureDatabaseCreatedAsync викидає явну помилку, а не пропуск тесту
    [Fact]
    public async Task EnsureDatabaseCreated_WhenHostOrPortUnreachable_ThrowsExplicitException()
    {
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

        var adminBuilder = new NpgsqlConnectionStringBuilder(TestEnvironment.ConnectionString)
        {
            Database = "postgres"
        };

        // 1. Гарантовано очищаємо можливу тестову БД перед початком операцій
        await TestEnvironment.DropDatabaseIfExistsAsync(targetDb);

        // 2. Перевіряємо права суперкористувача або CREATEROLE в адміністративному підключенні
        await using (var adminConn = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await adminConn.OpenAsync();

            await using var checkSuperCmd = new NpgsqlCommand(
                "SELECT COALESCE(bool_or(rolsuper OR rolcreaterole), false) FROM pg_roles WHERE rolname = current_user;", adminConn);
            var canManageRoles = await checkSuperCmd.ExecuteScalarAsync() as bool? ?? false;
            if (!canManageRoles)
            {
                throw new InvalidOperationException(
                    "[TEST CONFIGURATION ERROR]: Поточний тестовий користувач PostgreSQL не має прав суперкористувача або CREATEROLE, " +
                    "необхідних для налаштування тестової ролі без прав CREATEDB.");
            }
        }

        bool roleCreated = false;

        try
        {
            // 3. Створення ролі та GRANT всередині загальної структури try/finally
            await using (var adminConn = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await adminConn.OpenAsync();

                // Попереднє очищення ролі без порожніх catch
                await using var preCleanCmd = new NpgsqlCommand($@"
                    DO $$ BEGIN
                        IF EXISTS (SELECT FROM pg_roles WHERE rolname = '{unprivilegedUser}') THEN
                            EXECUTE 'REVOKE ALL PRIVILEGES ON DATABASE postgres FROM ""{unprivilegedUser}""';
                            EXECUTE 'DROP ROLE ""{unprivilegedUser}""';
                        END IF;
                    END $$;", adminConn);
                await preCleanCmd.ExecuteNonQueryAsync();

                await using var createRoleCmd = new NpgsqlCommand($@"
                    CREATE ROLE ""{unprivilegedUser}"" WITH LOGIN PASSWORD '{unprivilegedPass}' NOSUPERUSER NOCREATEDB NOCREATEROLE;
                    GRANT CONNECT ON DATABASE postgres TO ""{unprivilegedUser}"";", adminConn);
                await createRoleCmd.ExecuteNonQueryAsync();
                roleCreated = true;
            }

            var unprivilegedBuilder = new NpgsqlConnectionStringBuilder(TestEnvironment.ConnectionString)
            {
                Database = targetDb,
                Username = unprivilegedUser,
                Password = unprivilegedPass,
                Timeout = 5
            };

            // 4. Спроба створити тестову БД через користувача без прав CREATEDB
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TestEnvironment.EnsureDatabaseCreatedAsync(unprivilegedBuilder.ConnectionString));

            // 5. Перевіряємо зрозуміле повідомлення та точний SQLSTATE 42501
            Assert.Contains("DATABASE PERMISSION ERROR", ex.Message);
            Assert.Contains("CREATEDB", ex.Message);

            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("42501", pgEx.SqlState);
        }
        finally
        {
            // 6. Гарантоване очищення ресурсів без приховування помилок
            var cleanupErrors = new List<Exception>();

            try
            {
                await TestEnvironment.DropDatabaseIfExistsAsync(targetDb);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Test Cleanup Error]: Помилка видалення тимчасової БД '{targetDb}': {ex}");
                cleanupErrors.Add(new InvalidOperationException($"Помилка видалення тимчасової БД '{targetDb}': {ex.Message}", ex));
            }

            if (roleCreated)
            {
                try
                {
                    await using var adminConn = new NpgsqlConnection(adminBuilder.ConnectionString);
                    await adminConn.OpenAsync();

                    await using var dropRoleCmd = new NpgsqlCommand($@"
                        DO $$ BEGIN
                            IF EXISTS (SELECT FROM pg_roles WHERE rolname = '{unprivilegedUser}') THEN
                                EXECUTE 'REVOKE ALL PRIVILEGES ON DATABASE postgres FROM ""{unprivilegedUser}""';
                                EXECUTE 'DROP ROLE ""{unprivilegedUser}""';
                            END IF;
                        END $$;", adminConn);
                    await dropRoleCmd.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Test Cleanup Error]: Помилка видалення тимчасової ролі '{unprivilegedUser}': {ex}");
                    cleanupErrors.Add(new InvalidOperationException($"Помилка видалення тимчасової ролі '{unprivilegedUser}': {ex.Message}", ex));
                }
            }

            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Помилки під час гарантованого очищення тестових ресурсів у TestEnvironmentSafetyTests:", cleanupErrors);
            }
        }
    }

    // 6a. Перевірка конфігурації: TEST_ALLOW_UNSAFE_DB_OVERRIDE без CI-перемінних блокується до звернення до БД
    [Fact]
    public async Task IsDatabaseAvailableAsync_WhenUnsafeOverrideWithoutCi_ThrowsCriticalSafetyError()
    {
        string? originalOverride = Environment.GetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE");
        string? originalDb = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION_STRING");
        string? originalCi = Environment.GetEnvironmentVariable("CI");
        string? originalGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        string? originalGitlabCi = Environment.GetEnvironmentVariable("GITLAB_CI");
        string? originalTfBuild = Environment.GetEnvironmentVariable("TF_BUILD");

        try
        {
            Environment.SetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE", "true");
            Environment.SetEnvironmentVariable("CI", null);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
            Environment.SetEnvironmentVariable("GITLAB_CI", null);
            Environment.SetEnvironmentVariable("TF_BUILD", null);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestEnvironment.IsDatabaseAvailableAsync());
            Assert.Contains("CRITICAL SAFETY ERROR", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE", originalOverride);
            Environment.SetEnvironmentVariable("TEST_DB_CONNECTION_STRING", originalDb);
            Environment.SetEnvironmentVariable("CI", originalCi);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", originalGithubActions);
            Environment.SetEnvironmentVariable("GITLAB_CI", originalGitlabCi);
            Environment.SetEnvironmentVariable("TF_BUILD", originalTfBuild);
        }
    }

    // 6b. IsDatabaseAvailableAsync() обов'язково виконує етап створення/підготовки БД і прокидає помилки безпеки
    [Fact]
    public async Task IsDatabaseAvailableAsync_DoesNotSkipCreationPhaseAndPropagatesSafetyErrors()
    {
        bool creationPhaseInvoked = false;

        TestEnvironment.DatabaseReadyActionOverride = async () =>
        {
            creationPhaseInvoked = true;
            await Task.FromException(new InvalidOperationException("[CRITICAL SAFETY ERROR]: Підготовчий етап створення БД зазнав помилки безпеки"));
        };

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TestEnvironment.IsDatabaseAvailableAsync());

            Assert.True(creationPhaseInvoked, "Етап створення БД повинен обов'язково викликатися");
            Assert.Contains("CRITICAL SAFETY ERROR", ex.Message);
        }
        finally
        {
            TestEnvironment.DatabaseReadyActionOverride = null;
        }
    }

    // 7. IsDatabaseAvailableAsync() доводить строгий порядок: спочатку підготовка БД, потім відкриття/перевірка з'єднання
    //    Повністю ізольований від реального PostgreSQL
    [Fact]
    public async Task IsDatabaseAvailableAsync_ExecutesCreationPhaseBeforeConnectionCheck()
    {
        var callLog = new List<string>();

        TestEnvironment.DatabaseReadyActionOverride = async () =>
        {
            callLog.Add("1_preparation_phase");
            await Task.CompletedTask;
        };

        TestEnvironment.ConnectionCheckActionOverride = async (connStr) =>
        {
            callLog.Add("2_connection_check");
            await Task.CompletedTask;
            return true;
        };

        try
        {
            bool isAvailable = await TestEnvironment.IsDatabaseAvailableAsync();
            Assert.True(isAvailable);

            Assert.Equal(2, callLog.Count);
            Assert.Equal("1_preparation_phase", callLog[0]);
            Assert.Equal("2_connection_check", callLog[1]);
        }
        finally
        {
            TestEnvironment.DatabaseReadyActionOverride = null;
            TestEnvironment.ConnectionCheckActionOverride = null;
        }
    }

    // 8. IsDatabaseAvailableAsync() при збої створення БД через права CREATEDB явно прокидає виняток без маскування
    [Fact]
    public async Task IsDatabaseAvailableAsync_WhenCreationPhaseFailsWithPermissionError_PropagatesExceptionWithoutMasking()
    {
        TestEnvironment.DatabaseReadyActionOverride = () =>
            throw new InvalidOperationException("[DATABASE PERMISSION ERROR]: Користувач не має дозволу CREATEDB (SQLSTATE: 42501)");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TestEnvironment.IsDatabaseAvailableAsync());

            Assert.Contains("DATABASE PERMISSION ERROR", ex.Message);
            Assert.Contains("CREATEDB", ex.Message);
        }
        finally
        {
            TestEnvironment.DatabaseReadyActionOverride = null;
        }
    }

    // 9. IsDatabaseAvailableAsync() при звичайній мережевій недоступності PostgreSQL повертає false без маскування під помилку безпеки
    [Fact]
    public async Task IsDatabaseAvailableAsync_WhenHostOrPortUnreachable_ReturnsFalseWithoutThrowingSafetyError()
    {
        string unreachablePortConnStr = "Host=localhost;Port=5433;Database=dpc_c10_test;Username=dpc_user;Password=dpc_password;Timeout=1";

        bool isAvailable = await TestEnvironment.IsDatabaseAvailableAsync(unreachablePortConnStr);
        Assert.False(isAvailable);
    }

    // 10. IsDatabaseAvailableAsync() при невірному паролі (SQLSTATE 28P01) прокидає помилку і не перетворює її на false/Skip
    [SkippableFact]
    public async Task IsDatabaseAvailableAsync_WhenAuthenticationFailsWith28P01_ThrowsExplicitExceptionInsteadOfReturningFalse()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string badPasswordConnStr = "Host=localhost;Port=5432;Database=dpc_c10_test;Username=dpc_user;Password=wrong_invalid_password_c10;Timeout=2";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.IsDatabaseAvailableAsync(badPasswordConnStr));

        Assert.Contains("DATABASE AUTHENTICATION ERROR", ex.Message);
        Assert.Contains("28P01", ex.Message);
    }

    // 11. IsDatabaseAvailableAsync() при неіснуючому користувачі прокидає явний виняток і не перетворює на false
    [SkippableFact]
    public async Task IsDatabaseAvailableAsync_WhenUserDoesNotExist_ThrowsExplicitExceptionInsteadOfReturningFalse()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        string badUserConnStr = "Host=localhost;Port=5432;Database=dpc_c10_test;Username=nonexistent_test_user_xyz;Password=some_pass;Timeout=2";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.IsDatabaseAvailableAsync(badUserConnStr));

        Assert.Contains("DATABASE AUTHENTICATION ERROR", ex.Message);
        Assert.Contains("28P01", ex.Message);
    }

    // 12. Прямий виклик EnsureDatabaseCreatedAsync() з основною БД dpc_c10 суворо блокується
    [Fact]
    public async Task EnsureDatabaseCreatedAsync_TargetingMainAppDatabase_ThrowsCriticalSafetyError()
    {
        string mainDbConnStr = "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.EnsureDatabaseCreatedAsync(mainDbConnStr));

        Assert.Contains("CRITICAL SAFETY ERROR", ex.Message);
        Assert.Contains("dpc_c10", ex.Message);
    }

    // 13. Прямий виклик EnsureDatabaseCreatedAsync() з довільним ім'ям бази (у т.ч. що містить 'test') блокується
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

    // 14. Прямий виклик EnsureDatabaseCreatedAsync() з віддаленим хостом суворо блокується
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
