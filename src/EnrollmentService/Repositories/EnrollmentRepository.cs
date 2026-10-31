using EnrollmentService.Domain;
using EnrollmentService.Services;
using Npgsql;

namespace EnrollmentService.Repositories;

public sealed class EnrollmentRepository : IEnrollmentRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ServiceOptions _options;

    public EnrollmentRepository(IDbConnectionFactory connectionFactory, ServiceOptions options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<StudentEntity>> GetStudentsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var students = new List<StudentEntity>();

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, email, full_name, max_disciplines, active_count, active_ects
            FROM student
            ORDER BY id;", conn);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            students.Add(new StudentEntity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetDecimal(5)));
        }

        return students;
    }

    public async Task<StudentEntity?> GetStudentByIdAsync(string studentId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, email, full_name, max_disciplines, active_count, active_ects
            FROM student
            WHERE id = @studentId;", conn);
        cmd.Parameters.AddWithValue("studentId", studentId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StudentEntity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetDecimal(5));
    }

    public async Task<StudentEntity?> GetStudentForUpdateAsync(
        string studentId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, email, full_name, max_disciplines, active_count, active_ects
            FROM student
            WHERE id = @studentId
            FOR UPDATE;", conn, tx);
        cmd.Parameters.AddWithValue("studentId", studentId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StudentEntity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetDecimal(5));
    }

    public async Task UpdateStudentStatsAsync(
        string studentId,
        int countDelta,
        decimal ectsDelta,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        string sql = countDelta >= 0
            ? @"
                UPDATE student
                SET active_count = active_count + @countDelta,
                    active_ects = active_ects + @ectsDelta
                WHERE id = @studentId;"
            : @"
                UPDATE student
                SET active_count = GREATEST(0, active_count + @countDelta),
                    active_ects = GREATEST(0.0, active_ects + @ectsDelta)
                WHERE id = @studentId;";

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("countDelta", countDelta);
        cmd.Parameters.AddWithValue("ectsDelta", ectsDelta);
        cmd.Parameters.AddWithValue("studentId", studentId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasActiveEnrollmentForDisciplineAsync(
        string studentId,
        string disciplineId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT 1 FROM enrollment
            WHERE student_id = @studentId AND discipline_id = @disciplineId AND status IN ('held', 'confirmed')
            LIMIT 1;", conn, tx);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("disciplineId", disciplineId);

        var obj = await cmd.ExecuteScalarAsync(cancellationToken);
        return obj != null;
    }

    public async Task<IReadOnlyList<(Guid Id, string GroupId)>> GetActiveEnrollmentsForDisciplineAsync(
        string studentId,
        string disciplineId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        var list = new List<(Guid Id, string GroupId)>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, group_id
            FROM enrollment
            WHERE student_id = @studentId
              AND discipline_id = @disciplineId
              AND status IN ('held', 'confirmed')
            FOR UPDATE;", conn, tx);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("disciplineId", disciplineId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return list;
    }

    public async Task<IReadOnlyList<WebEnrollmentResponseDto>> GetStudentEnrollmentsCursorAsync(
        string studentId,
        int limit,
        DateTimeOffset? cursorCreatedAt,
        Guid? cursorId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        bool hasCursor = cursorCreatedAt.HasValue && cursorId.HasValue;

        string sql = hasCursor
            ? @"
                SELECT e.id, e.student_id, e.group_id, e.discipline_id, d.title, d.ects,
                       e.status, e.version, e.created_at, e.expires_at, g.university_tz
                FROM enrollment e
                JOIN discipline d ON d.id = e.discipline_id
                JOIN discipline_group g ON g.id = e.group_id
                WHERE e.student_id = @studentId
                  AND (e.created_at < @cursorCreatedAt OR (e.created_at = @cursorCreatedAt AND e.id < @cursorId))
                ORDER BY e.created_at DESC, e.id DESC
                LIMIT @fetchLimit;"
            : @"
                SELECT e.id, e.student_id, e.group_id, e.discipline_id, d.title, d.ects,
                       e.status, e.version, e.created_at, e.expires_at, g.university_tz
                FROM enrollment e
                JOIN discipline d ON d.id = e.discipline_id
                JOIN discipline_group g ON g.id = e.group_id
                WHERE e.student_id = @studentId
                ORDER BY e.created_at DESC, e.id DESC
                LIMIT @fetchLimit;";

        var items = new List<WebEnrollmentResponseDto>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("fetchLimit", limit + 1);
        if (hasCursor)
        {
            cmd.Parameters.AddWithValue("cursorCreatedAt", cursorCreatedAt!.Value);
            cmd.Parameters.AddWithValue("cursorId", cursorId!.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            Guid rowId = reader.GetGuid(0);
            string sId = reader.GetString(1);
            string gId = reader.GetString(2);
            string dId = reader.GetString(3);
            string dTitle = reader.GetString(4);
            decimal ects = reader.GetDecimal(5);
            string status = reader.GetString(6);
            int version = reader.GetInt32(7);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(8);
            var expiresAt = reader.GetFieldValue<DateTimeOffset>(9);
            string tz = reader.GetString(10);

            items.Add(new WebEnrollmentResponseDto(
                rowId.ToString("D"),
                sId,
                gId,
                dId,
                dTitle,
                ects,
                status,
                version,
                createdAt,
                expiresAt,
                EnrollmentDomainRules.FormatInUniversityTimeZone(createdAt, tz),
                EnrollmentDomainRules.FormatInUniversityTimeZone(expiresAt, tz),
                tz,
                _options.InstanceId));
        }

        return items;
    }

    public async Task<WebEnrollmentResponseDto?> GetEnrollmentByIdAsync(
        Guid id,
        string studentId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT e.id, e.student_id, e.group_id, e.discipline_id, d.title, d.ects,
                   e.status, e.version, e.created_at, e.expires_at, g.university_tz
            FROM enrollment e
            JOIN discipline d ON d.id = e.discipline_id
            JOIN discipline_group g ON g.id = e.group_id
            WHERE e.id = @id AND e.student_id = @studentId;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("studentId", studentId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var createdAt = reader.GetFieldValue<DateTimeOffset>(8);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(9);
        string tz = reader.GetString(10);

        return new WebEnrollmentResponseDto(
            reader.GetGuid(0).ToString("D"),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDecimal(5),
            reader.GetString(6),
            reader.GetInt32(7),
            createdAt,
            expiresAt,
            EnrollmentDomainRules.FormatInUniversityTimeZone(createdAt, tz),
            EnrollmentDomainRules.FormatInUniversityTimeZone(expiresAt, tz),
            tz,
            _options.InstanceId);
    }

    public async Task<(string StudentId, string GroupId)?> GetEnrollmentLookupAsync(
        Guid id,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT student_id, group_id FROM enrollment WHERE id = @id;", conn, tx);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    public async Task<WebEnrollmentResponseDto?> GetEnrollmentForUpdateAsync(
        Guid id,
        string studentId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT e.id, e.student_id, e.group_id, e.discipline_id, d.title, d.ects,
                   e.status, e.version, e.created_at, e.expires_at, g.university_tz
            FROM enrollment e
            JOIN discipline d ON d.id = e.discipline_id
            JOIN discipline_group g ON g.id = e.group_id
            WHERE e.id = @id AND e.student_id = @studentId
            FOR UPDATE OF e;", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("studentId", studentId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var createdAt = reader.GetFieldValue<DateTimeOffset>(8);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(9);
        string tz = reader.GetString(10);

        return new WebEnrollmentResponseDto(
            reader.GetGuid(0).ToString("D"),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDecimal(5),
            reader.GetString(6),
            reader.GetInt32(7),
            createdAt,
            expiresAt,
            EnrollmentDomainRules.FormatInUniversityTimeZone(createdAt, tz),
            EnrollmentDomainRules.FormatInUniversityTimeZone(expiresAt, tz),
            tz,
            _options.InstanceId);
    }

    public async Task CreateEnrollmentAsync(
        Guid id,
        string studentId,
        string groupId,
        string disciplineId,
        string status,
        int version,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO enrollment (id, student_id, group_id, discipline_id, status, version, created_at, expires_at)
            VALUES (@id, @studentId, @groupId, @disciplineId, @status, @version, @createdAt, @expiresAt);", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("groupId", groupId);
        cmd.Parameters.AddWithValue("disciplineId", disciplineId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("createdAt", createdAt);
        cmd.Parameters.AddWithValue("expiresAt", expiresAt);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CancelEnrollmentAsync(
        Guid id,
        int newVersion,
        DateTimeOffset now,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE enrollment
            SET status = 'cancelled', version = @version, cancelled_at = @now
            WHERE id = @id;", conn, tx);
        cmd.Parameters.AddWithValue("version", newVersion);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("id", id);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ConfirmEnrollmentHeldAsync(
        Guid id,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE enrollment
            SET status = 'confirmed', version = version + 1
            WHERE id = @enrollmentId AND status = 'held';", conn, tx);
        cmd.Parameters.AddWithValue("enrollmentId", id);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetEnrollmentsAndStudentsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            DELETE FROM enrollment;
            UPDATE student SET active_count = 0, active_ects = 0.0 WHERE id IN ('student-1', 'student-2', 'student-3');
            UPDATE student SET active_count = 2, active_ects = 20.0 WHERE id = 'student-ects-20';
            UPDATE student SET active_count = 5, active_ects = 25.0 WHERE id = 'student-limit-max';", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
