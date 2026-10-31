using Npgsql;

namespace EnrollmentService.Repositories;

public sealed class EnrollmentRequestRepository : IEnrollmentRequestRepository
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
            "SELECT id, student_id, group_id, status, created_at, second_choice_group_id FROM enrollment_requests WHERE id = @id;", conn);
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
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public async Task<EnrollmentStatusResponseDto?> MarkFirstReadAndGetStatusAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE enrollment_requests
            SET first_read_at = COALESCE(first_read_at, @now)
            WHERE id = @id AND status IN ('done', 'rejected', 'failed')
            RETURNING id, student_id, group_id, status, rejection_reason,
                      created_at, started_at, finished_at, first_read_at,
                      second_choice_group_id, assigned_group_id,
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
        string? secondChoice = reader.IsDBNull(9) ? null : reader.GetString(9);
        string? assignedGroup = reader.IsDBNull(10) ? null : reader.GetString(10);
        bool recordedInRoster = reader.GetBoolean(11);

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
            secondChoice,
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
                   second_choice_group_id, assigned_group_id,
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
        string? actSecondChoice = selectReader.IsDBNull(9) ? null : selectReader.GetString(9);
        string? actAssignedGroup = selectReader.IsDBNull(10) ? null : selectReader.GetString(10);
        bool actRecordedInRoster = selectReader.GetBoolean(11);

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
            actSecondChoice,
            actAssignedGroup,
            actRecordedInRoster);
    }

    public async Task InsertQueuedRequestAsync(
        QueuedRequestItem item,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO enrollment_requests (id, student_id, group_id, second_choice_group_id, status, created_at)
            VALUES (@id, @studentId, @groupId, @secondChoiceGroupId, 'queued', @createdAt);", conn);
        insertCmd.Parameters.AddWithValue("id", item.Id);
        insertCmd.Parameters.AddWithValue("studentId", item.StudentId);
        insertCmd.Parameters.AddWithValue("groupId", item.GroupId);
        insertCmd.Parameters.Add(new NpgsqlParameter("secondChoiceGroupId", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)item.SecondChoiceGroupId ?? DBNull.Value
        });
        insertCmd.Parameters.AddWithValue("createdAt", item.CreatedAtUtc);

        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task MarkStartedAsync(
        Guid id,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "UPDATE enrollment_requests SET status = 'processing', started_at = @startedAt WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("startedAt", startedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        await using var updateReqCmd = new NpgsqlCommand(@"
            UPDATE enrollment_requests
            SET status = @status,
                rejection_reason = @reason,
                assigned_group_id = COALESCE(@assignedGroupId, assigned_group_id),
                finished_at = @finishedAt
            WHERE id = @id;", conn, tx);
        updateReqCmd.Parameters.AddWithValue("id", id);
        updateReqCmd.Parameters.AddWithValue("status", status);
        updateReqCmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)rejectionReason ?? DBNull.Value
        });
        updateReqCmd.Parameters.Add(new NpgsqlParameter("assignedGroupId", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)assignedGroupId ?? DBNull.Value
        });
        updateReqCmd.Parameters.AddWithValue("finishedAt", finishedAt);
        await updateReqCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertSecondChoiceRequestAsync(
        Guid id,
        string studentId,
        string primaryGroupId,
        string secondChoiceGroupId,
        DateTimeOffset now,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var upsertCmd = new NpgsqlCommand(@"
            INSERT INTO enrollment_requests (
                id, student_id, group_id, second_choice_group_id, status, created_at, started_at
            ) VALUES (
                @id, @studentId, @primaryGroupId, @secondChoiceGroupId, 'processing', @now, @now
            )
            ON CONFLICT (id) DO UPDATE
            SET second_choice_group_id = EXCLUDED.second_choice_group_id;", conn);
        upsertCmd.Parameters.AddWithValue("id", id);
        upsertCmd.Parameters.AddWithValue("studentId", studentId);
        upsertCmd.Parameters.AddWithValue("primaryGroupId", primaryGroupId);
        upsertCmd.Parameters.AddWithValue("secondChoiceGroupId", secondChoiceGroupId);
        upsertCmd.Parameters.AddWithValue("now", now);
        await upsertCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(Guid RequestId, string StudentId, string SecondChoiceGroupId)>> GetSecondChoiceCandidatesAsync(
        string primaryGroupId,
        Guid? filterRequestId,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<(Guid RequestId, string StudentId, string SecondChoiceGroupId)>();
        string candidateSql = filterRequestId.HasValue
            ? @"
                SELECT r.id, r.student_id, r.second_choice_group_id
                FROM enrollment_requests r
                WHERE r.id = @requestId
                  AND r.group_id = @primaryGroupId
                  AND r.second_choice_group_id IS NOT NULL
                ORDER BY r.created_at ASC, r.id ASC;"
            : @"
                SELECT r.id, r.student_id, r.second_choice_group_id
                FROM enrollment_requests r
                LEFT JOIN decanate_roster dr ON dr.request_id = r.id
                WHERE r.group_id = @primaryGroupId
                  AND r.second_choice_group_id IS NOT NULL
                  AND dr.request_id IS NULL
                ORDER BY r.created_at ASC, r.id ASC;";

        await using var candCmd = new NpgsqlCommand(candidateSql, conn);
        candCmd.Parameters.AddWithValue("primaryGroupId", primaryGroupId);
        if (filterRequestId.HasValue)
        {
            candCmd.Parameters.AddWithValue("requestId", filterRequestId.Value);
        }

        await using var candReader = await candCmd.ExecuteReaderAsync(cancellationToken);
        while (await candReader.ReadAsync(cancellationToken))
        {
            candidates.Add((
                candReader.GetGuid(0),
                candReader.GetString(1),
                candReader.GetString(2)));
        }

        return candidates;
    }

    public async Task ResetRequestsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"DELETE FROM enrollment_requests;", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
