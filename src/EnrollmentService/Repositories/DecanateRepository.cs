using Npgsql;

namespace EnrollmentService.Repositories;

public sealed class DecanateRepository : IDecanateRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DecanateRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<string?> GetRosterGroupForRequestAsync(
        Guid requestId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT group_id FROM decanate_roster WHERE request_id = @requestId;", conn, tx);
        cmd.Parameters.AddWithValue("requestId", requestId);

        var obj = await cmd.ExecuteScalarAsync(cancellationToken);
        return obj as string;
    }

    public async Task<(string GroupId, Guid? EnrollmentId)?> GetRosterAssignmentForRequestAsync(
        Guid requestId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT group_id, enrollment_id FROM decanate_roster WHERE request_id = @requestId;", conn, tx);
        cmd.Parameters.AddWithValue("requestId", requestId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string grp = reader.GetString(0);
        Guid? enrId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        return (grp, enrId);
    }

    public async Task<(bool AlreadyInRoster, int OtherDisciplineCount, decimal OtherEcts)> GetStudentRosterStatsAsync(
        string studentId,
        string disciplineId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                EXISTS(SELECT 1 FROM decanate_roster WHERE student_id = @studentId AND discipline_id = @disciplineId),
                COUNT(DISTINCT discipline_id)::INT,
                COALESCE(SUM(ects), 0.0)
            FROM decanate_roster
            WHERE student_id = @studentId AND discipline_id <> @disciplineId;", conn, tx);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("disciplineId", disciplineId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (false, 0, 0.0m);
        }

        return (reader.GetBoolean(0), reader.GetInt32(1), reader.GetDecimal(2));
    }

    public async Task<int> GetGroupRosterCountAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*)::INT FROM decanate_roster WHERE group_id = @groupId;", conn, tx);
        cmd.Parameters.AddWithValue("groupId", groupId);

        var val = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(val);
    }

    public async Task InsertRosterEntryAsync(
        Guid requestId,
        string studentId,
        string groupId,
        string disciplineId,
        decimal ects,
        Guid? enrollmentId,
        string assignmentType,
        DateTimeOffset createdAt,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO decanate_roster (
                request_id, student_id, group_id, discipline_id, ects, enrollment_id, assignment_type, created_at
            ) VALUES (
                @requestId, @studentId, @groupId, @disciplineId, @ects, @enrollmentId, @assignmentType, @createdAt
            )
            ON CONFLICT (request_id) DO NOTHING;", conn, tx);
        cmd.Parameters.AddWithValue("requestId", requestId);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("groupId", groupId);
        cmd.Parameters.AddWithValue("disciplineId", disciplineId);
        cmd.Parameters.AddWithValue("ects", ects);
        cmd.Parameters.Add(new NpgsqlParameter("enrollmentId", NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = (object?)enrollmentId ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("assignmentType", assignmentType);
        cmd.Parameters.AddWithValue("createdAt", createdAt);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteRosterEntryForEnrollmentAsync(
        Guid enrollmentId,
        string studentId,
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            DELETE FROM decanate_roster
            WHERE enrollment_id = @enrollmentId
               OR (student_id = @studentId AND group_id = @groupId);", conn, tx);
        cmd.Parameters.AddWithValue("enrollmentId", enrollmentId);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("groupId", groupId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DecanateRosterEntryDto>> GetRosterEntriesAsync(
        string? groupId,
        string? studentId,
        Guid? requestId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT dr.request_id, dr.student_id, dr.group_id, dr.discipline_id, d.title,
                   dr.ects, dr.enrollment_id, dr.assignment_type, dr.created_at
            FROM decanate_roster dr
            JOIN discipline d ON d.id = dr.discipline_id
            WHERE (@groupId IS NULL OR dr.group_id = @groupId)
              AND (@studentId IS NULL OR dr.student_id = @studentId)
              AND (@requestId IS NULL OR dr.request_id = @requestId)
            ORDER BY dr.created_at ASC, dr.request_id ASC;", conn);
        cmd.Parameters.Add(new NpgsqlParameter("groupId", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(groupId) ? DBNull.Value : groupId.Trim()
        });
        cmd.Parameters.Add(new NpgsqlParameter("studentId", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(studentId) ? DBNull.Value : studentId.Trim()
        });
        cmd.Parameters.Add(new NpgsqlParameter("requestId", NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = (object?)requestId ?? DBNull.Value
        });

        var items = new List<DecanateRosterEntryDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DecanateRosterEntryDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6).ToString("D"),
                reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return items;
    }

    public async Task ResetRosterAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"DELETE FROM decanate_roster;", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
