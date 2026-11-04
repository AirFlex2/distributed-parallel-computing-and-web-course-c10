using Npgsql;

namespace EnrollmentService.Repositories;

public sealed record ExistingRequestRecord(
    Guid Id,
    string StudentId,
    string GroupId,
    string Status,
    DateTimeOffset CreatedAt,
    string? RequestPayloadHash,
    string? IdempotencyKey
);

public sealed class EnrollmentRequestRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public EnrollmentRequestRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<EnrollmentAcceptedResponseDto?> GetAcceptedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, student_id, group_id, status, created_at FROM enrollment_requests WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EnrollmentAcceptedResponseDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task<ExistingRequestRecord?> GetRecordByIdAsync(
        Guid id,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, student_id, group_id, status, created_at, request_payload_hash, idempotency_key
            FROM enrollment_requests
            WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingRequestRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task<ExistingRequestRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, student_id, group_id, status, created_at, request_payload_hash, idempotency_key
            FROM enrollment_requests
            WHERE idempotency_key = @idempotencyKey;", conn);
        cmd.Parameters.AddWithValue("idempotencyKey", idempotencyKey);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingRequestRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task<EnrollmentStatusResponseDto?> MarkFirstReadAndGetStatusAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE enrollment_requests
            SET first_read_at = CASE
                WHEN status IN ('done', 'rejected', 'failed') AND first_read_at IS NULL THEN @now
                ELSE first_read_at
            END
            WHERE id = @id
            RETURNING id, student_id, group_id, status, rejection_reason,
                      created_at, started_at, finished_at, first_read_at,
                      assigned_group_id,
                      EXISTS(SELECT 1 FROM decanate_roster dr WHERE dr.request_id = enrollment_requests.id);", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", now);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var createdAt = reader.GetFieldValue<DateTimeOffset>(5);
        var startedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6);
        var finishedAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(7);
        var firstReadAt = reader.IsDBNull(8) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(8);
        string? assignedGroup = reader.IsDBNull(9) ? null : reader.GetString(9);
        bool recordedInRoster = reader.GetBoolean(10);

        return new EnrollmentStatusResponseDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            createdAt,
            startedAt,
            finishedAt,
            firstReadAt,
            null,
            null,
            null,
            assignedGroup,
            recordedInRoster);
    }

    public async Task<EnrollmentStatusResponseDto?> GetStatusByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var selectCmd = new NpgsqlCommand(@"
            SELECT id, student_id, group_id, status, rejection_reason,
                   created_at, started_at, finished_at, first_read_at,
                   assigned_group_id,
                   EXISTS(SELECT 1 FROM decanate_roster dr WHERE dr.request_id = enrollment_requests.id)
            FROM enrollment_requests
            WHERE id = @id;", conn);
        selectCmd.Parameters.AddWithValue("id", id);

        await using var selectReader = await selectCmd.ExecuteReaderAsync(cancellationToken);
        if (!await selectReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var actCreatedAt = selectReader.GetFieldValue<DateTimeOffset>(5);
        var actStartedAt = selectReader.IsDBNull(6) ? (DateTimeOffset?)null : selectReader.GetFieldValue<DateTimeOffset>(6);
        var actFinishedAt = selectReader.IsDBNull(7) ? (DateTimeOffset?)null : selectReader.GetFieldValue<DateTimeOffset>(7);
        var actFirstReadAt = selectReader.IsDBNull(8) ? (DateTimeOffset?)null : selectReader.GetFieldValue<DateTimeOffset>(8);
        string? actAssignedGroup = selectReader.IsDBNull(9) ? null : selectReader.GetString(9);
        bool actRecordedInRoster = selectReader.GetBoolean(10);

        return new EnrollmentStatusResponseDto(
            selectReader.GetGuid(0),
            selectReader.GetString(1),
            selectReader.GetString(2),
            selectReader.GetString(3),
            selectReader.IsDBNull(4) ? null : selectReader.GetString(4),
            actCreatedAt,
            actStartedAt,
            actFinishedAt,
            actFirstReadAt,
            null,
            null,
            null,
            actAssignedGroup,
            actRecordedInRoster);
    }

    public async Task<bool> TryInsertQueuedRequestAsync(
        QueuedRequestItem item,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var insertCmd = new NpgsqlCommand(@"
                INSERT INTO enrollment_requests (
                    id, student_id, group_id, status, created_at, idempotency_key, request_payload_hash, enrollment_id
                ) VALUES (
                    @id, @studentId, @groupId, 'queued', @createdAt, @idempotencyKey, @payloadHash, @enrollmentId
                )
                ON CONFLICT (id) DO NOTHING
                RETURNING id;", conn);
            insertCmd.Parameters.AddWithValue("id", item.Id);
            insertCmd.Parameters.AddWithValue("studentId", item.StudentId);
            insertCmd.Parameters.AddWithValue("groupId", item.GroupId);
            insertCmd.Parameters.AddWithValue("createdAt", item.CreatedAtUtc);
            insertCmd.Parameters.Add(new NpgsqlParameter("idempotencyKey", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = (object?)item.IdempotencyKey ?? DBNull.Value
            });
            insertCmd.Parameters.Add(new NpgsqlParameter("payloadHash", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = (object?)item.RequestPayloadHash ?? DBNull.Value
            });
            insertCmd.Parameters.Add(new NpgsqlParameter("enrollmentId", NpgsqlTypes.NpgsqlDbType.Uuid)
            {
                Value = (object?)item.EnrollmentId ?? DBNull.Value
            });

            var result = await insertCmd.ExecuteScalarAsync(cancellationToken);
            return result != null;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Унікальний конфлікт по student_id + idempotency_key
            return false;
        }
    }

    public async Task DeleteRequestAsync(
        Guid id,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var deleteCmd = new NpgsqlCommand(
            "DELETE FROM enrollment_requests WHERE id = @id;", conn);
        deleteCmd.Parameters.AddWithValue("id", id);
        await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<QueuedRequestItem>> GetQueuedRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, student_id, group_id, created_at, idempotency_key, request_payload_hash, enrollment_id
            FROM enrollment_requests
            WHERE status = 'queued'
            ORDER BY created_at ASC, id ASC
            LIMIT @limit;", conn);
        cmd.Parameters.AddWithValue("limit", limit);

        var list = new List<QueuedRequestItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new QueuedRequestItem
            {
                Id = reader.GetGuid(0),
                StudentId = reader.GetString(1),
                GroupId = reader.GetString(2),
                CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(3),
                IdempotencyKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                RequestPayloadHash = reader.IsDBNull(5) ? null : reader.GetString(5),
                EnrollmentId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                EnqueueTimestamp = System.Diagnostics.Stopwatch.GetTimestamp()
            });
        }
        return list;
    }

    public async Task<bool> TryMarkStartedAsync(
        Guid id,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE enrollment_requests
            SET status = 'processing', started_at = @startedAt
            WHERE id = @id AND status = 'queued';", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("startedAt", startedAt);
        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task MarkStartedAsync(
        Guid id,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        await TryMarkStartedAsync(id, startedAt, cancellationToken);
    }

    public async Task UpdateStatusAsync(
        Guid id,
        string status,
        string? rejectionReason,
        string? assignedGroupId,
        DateTimeOffset finishedAt,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE enrollment_requests
            SET status = @status,
                rejection_reason = @rejectionReason,
                assigned_group_id = @assignedGroupId,
                finished_at = @finishedAt
            WHERE id = @id;", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter("rejectionReason", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)rejectionReason ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("assignedGroupId", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)assignedGroupId ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("finishedAt", finishedAt);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RecoverStuckProcessingRequestsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE enrollment_requests r
            SET status = CASE
                    WHEN EXISTS (SELECT 1 FROM decanate_roster d WHERE d.request_id = r.id) THEN 'done'
                    ELSE 'failed'
                END,
                rejection_reason = CASE
                    WHEN EXISTS (SELECT 1 FROM decanate_roster d WHERE d.request_id = r.id) THEN NULL
                    ELSE 'Обробку перервано аварійним перезапуском сервісу до завершення.'
                END,
                finished_at = COALESCE(finished_at, NOW())
            WHERE status = 'processing';", conn);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetRequestsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM enrollment_requests;", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
