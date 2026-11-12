using Npgsql;

namespace EnrollmentService.Repositories;

public interface IDbConnectionFactory
{
    Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly ServiceOptions _options;

    public NpgsqlConnectionFactory(ServiceOptions options)
    {
        _options = options;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new NpgsqlConnection(_options.DbConnectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }
}
