using Npgsql;

namespace EnrollmentService.Repositories;

public sealed record WebUserRecord(
    string Id,
    string Username,
    string Email,
    string FullName,
    string Role,
    string PasswordHash,
    string PasswordSalt,
    string? StudentId
);

public sealed record AuthenticatedWebUser(
    string UserId,
    string Username,
    string Role,
    string? StudentId,
    string TokenHash,
    DateTimeOffset ExpiresAt
);

public sealed class AuthRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AuthRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<WebUserRecord?> GetUserByLoginAsync(string login, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, username, email, full_name, role, password_hash, password_salt, student_id
            FROM web_users
            WHERE username = @login OR id = @login
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("login", login);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WebUserRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async Task<WebUserRecord?> GetUserByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, username, email, full_name, role, password_hash, password_salt, student_id
            FROM web_users
            WHERE id = @userId
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("userId", userId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WebUserRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async Task UpdateRolePasswordsAsync(
        string role,
        string passwordHash,
        string passwordSalt,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE web_users
            SET password_hash = @hash,
                password_salt = @salt
            WHERE role = @role;", conn);
        cmd.Parameters.AddWithValue("hash", passwordHash);
        cmd.Parameters.AddWithValue("salt", passwordSalt);
        cmd.Parameters.AddWithValue("role", role);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateTokenAsync(
        string tokenHash,
        string userId,
        string username,
        string role,
        string? studentId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO web_auth_tokens (
                token_hash, user_id, username, role, student_id, created_at, expires_at, revoked_at
            ) VALUES (
                @tokenHash, @userId, @username, @role, @studentId, NOW(), @expiresAt, NULL
            );", conn);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("username", username);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.Add(new NpgsqlParameter("studentId", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)studentId ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("expiresAt", expiresAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AuthenticatedWebUser?> ValidateTokenAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT t.user_id, t.username, t.role, t.student_id, t.token_hash, t.expires_at
            FROM web_auth_tokens t
            JOIN web_users u ON u.id = t.user_id
            WHERE t.token_hash = @tokenHash
              AND t.revoked_at IS NULL
              AND t.expires_at > NOW()
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AuthenticatedWebUser(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5));
    }

    public async Task<bool> RevokeTokenAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE web_auth_tokens
            SET revoked_at = NOW()
            WHERE token_hash = @tokenHash
              AND revoked_at IS NULL;", conn);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);
        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }
}
