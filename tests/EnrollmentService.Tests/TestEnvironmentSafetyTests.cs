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

    // 3. Підключення до основної БД dpc_c10 суворо відхиляється
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

    // 4. Підключення до бази з довільною назвою, що містить 'test', відхиляється
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

    // 5. Відсутність прав на створення БД (або недійсні облікові дані) призводить до явної помилки, а не до пропуску тесту
    [Fact]
    public async Task EnsureDatabaseCreated_WhenCredentialsInvalidOrLacksPermissions_ThrowsExplicitException()
    {
        // Неіснуючий порт/хост зі збоєм підключення до службових баз
        string invalidConnStr = "Host=localhost;Port=5433;Database=dpc_c10_test;Username=invalid_user;Password=invalid_pass;Timeout=1";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.EnsureDatabaseCreatedAsync(invalidConnStr));

        Assert.NotNull(ex);
        Assert.Contains("Не вдалося перевірити або створити тестову БД", ex.Message);
    }

    // 6. IsDatabaseAvailableAsync() не пропускає етап створення БД і не глушить помилки безпеки
    [Fact]
    public async Task IsDatabaseAvailableAsync_DoesNotSkipCreationPhaseAndPropagatesSafetyErrors()
    {
        // Зберігаємо оригінальне значення
        string? originalOverride = Environment.GetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE");
        string? originalDb = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION_STRING");

        try
        {
            // Спроба використати TEST_ALLOW_UNSAFE_DB_OVERRIDE локально без змінної CI=true
            Environment.SetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE", "true");
            Environment.SetEnvironmentVariable("CI", null);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);

            // IsDatabaseAvailableAsync() зобов'язаний негайно викинути InvalidOperationException,
            // а не проковтнути помилку через catch і повернути false (що спричинило б пропуск тесту через Skip.If)
            await Assert.ThrowsAsync<InvalidOperationException>(() => TestEnvironment.IsDatabaseAvailableAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_ALLOW_UNSAFE_DB_OVERRIDE", originalOverride);
            Environment.SetEnvironmentVariable("TEST_DB_CONNECTION_STRING", originalDb);
        }
    }
}
