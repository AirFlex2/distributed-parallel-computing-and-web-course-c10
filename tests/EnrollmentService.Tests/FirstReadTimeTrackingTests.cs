using EnrollmentService.Repositories;
using Npgsql;
using Xunit;

namespace EnrollmentService.Tests;

public sealed class FirstReadTimeTrackingTests
{
    private const string ConnectionString = "Host=localhost;Port=5432;Database=dpc_c10;Username=dpc_user;Password=dpc_password;Maximum Pool Size=20";

    private sealed class TestConnectionFactory : IDbConnectionFactory
    {
        public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync(cancellationToken);
            return conn;
        }
    }

    private readonly TestConnectionFactory _factory = new();
    private readonly EnrollmentRequestRepository _repo;

    public FirstReadTimeTrackingTests()
    {
        _repo = new EnrollmentRequestRepository(_factory);
    }

    private async Task<bool> IsDatabaseAvailableAsync()
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task InsertTestRequestAsync(Guid id, string status, DateTimeOffset? firstReadAt = null)
    {
        await using var conn = await _factory.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO enrollment_requests (
                id, student_id, group_id, status, created_at, first_read_at
            ) VALUES (
                @id, 'student-1', 'DISC-WEB-01', @status, NOW(), @firstReadAt
            )
            ON CONFLICT (id) DO UPDATE SET status = @status, first_read_at = @firstReadAt;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter("firstReadAt", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = (object?)firstReadAt ?? DBNull.Value
        });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DeleteTestRequestAsync(Guid id)
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM enrollment_requests WHERE id = @id;", conn);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    // 1. Перший GET при queued фіксує t4
    [SkippableFact]
    public async Task FirstGet_WhenStatusIsQueued_FixesT4()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "queued", null);
            var t4 = DateTimeOffset.UtcNow;

            var result = await _repo.MarkFirstReadAndGetStatusAsync(id, t4);

            Assert.NotNull(result);
            Assert.Equal("queued", result.Status);
            Assert.NotNull(result.FirstReadAt);
            Assert.Equal(t4.ToUnixTimeMilliseconds(), result.FirstReadAt.Value.ToUnixTimeMilliseconds());
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // 2. Перший GET при processing фіксує t4
    [SkippableFact]
    public async Task FirstGet_WhenStatusIsProcessing_FixesT4()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "processing", null);
            var t4 = DateTimeOffset.UtcNow;

            var result = await _repo.MarkFirstReadAndGetStatusAsync(id, t4);

            Assert.NotNull(result);
            Assert.Equal("processing", result.Status);
            Assert.NotNull(result.FirstReadAt);
            Assert.Equal(t4.ToUnixTimeMilliseconds(), result.FirstReadAt.Value.ToUnixTimeMilliseconds());
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // 3. Перший GET при done фіксує t4
    [SkippableFact]
    public async Task FirstGet_WhenStatusIsDone_FixesT4()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "done", null);
            var t4 = DateTimeOffset.UtcNow;

            var result = await _repo.MarkFirstReadAndGetStatusAsync(id, t4);

            Assert.NotNull(result);
            Assert.Equal("done", result.Status);
            Assert.NotNull(result.FirstReadAt);
            Assert.Equal(t4.ToUnixTimeMilliseconds(), result.FirstReadAt.Value.ToUnixTimeMilliseconds());
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // 4. Повторний GET не змінює t4
    [SkippableFact]
    public async Task SubsequentGet_DoesNotModifyInitialT4()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "queued", null);
            var initialT4 = DateTimeOffset.UtcNow.AddMinutes(-5);
            var laterT4 = DateTimeOffset.UtcNow;

            // Перше читання
            var firstResult = await _repo.MarkFirstReadAndGetStatusAsync(id, initialT4);
            Assert.NotNull(firstResult);
            Assert.Equal(initialT4.ToUnixTimeMilliseconds(), firstResult.FirstReadAt!.Value.ToUnixTimeMilliseconds());

            // Повторне читання з пізнішим часом
            var secondResult = await _repo.MarkFirstReadAndGetStatusAsync(id, laterT4);
            Assert.NotNull(secondResult);
            Assert.Equal(initialT4.ToUnixTimeMilliseconds(), secondResult.FirstReadAt!.Value.ToUnixTimeMilliseconds());
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // 5. Одночасні GET не перезаписують початкову відмітку
    [SkippableFact]
    public async Task ConcurrentGets_DoNotOverwriteInitialTimestamp()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var id = Guid.NewGuid();
        try
        {
            await InsertTestRequestAsync(id, "processing", null);
            var baseTime = DateTimeOffset.UtcNow;

            // 10 одночасних конкурентних викликів з різними відмітками часу
            var tasks = Enumerable.Range(1, 10).Select(async i =>
            {
                var now = baseTime.AddMilliseconds(i * 100);
                return await _repo.MarkFirstReadAndGetStatusAsync(id, now);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.NotNull(r));
            // Усі відповіді повинні отримати одну й ту саму зафіксовану в БД первинну мітку
            var firstRecorded = results[0]!.FirstReadAt;
            Assert.NotNull(firstRecorded);
            Assert.All(results, r => Assert.Equal(firstRecorded.Value.ToUnixTimeMilliseconds(), r!.FirstReadAt!.Value.ToUnixTimeMilliseconds()));
        }
        finally
        {
            await DeleteTestRequestAsync(id);
        }
    }

    // 6. GET неіснуючої заявки не створює відмітку і повертає null
    [SkippableFact]
    public async Task GetNonExistentRequest_ReturnsNull_DoesNotCreateTimestamp()
    {
        Skip.If(!await IsDatabaseAvailableAsync(), "PostgreSQL is not accessible on localhost:5432. Integration test skipped.");

        var nonExistentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var result = await _repo.MarkFirstReadAndGetStatusAsync(nonExistentId, now);

        Assert.Null(result);
    }
}
