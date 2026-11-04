using EnrollmentService.Domain;
using EnrollmentService.Services;
using Npgsql;

namespace EnrollmentService.Repositories;

public sealed class EnrollmentRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ServiceOptions _options;

    public EnrollmentRepository(IDbConnectionFactory connectionFactory, ServiceOptions options)
    {
        _connectionFactory = connectionFactory;
        _options = options;
    }

    private WebEnrollmentResponseDto MapEnrollment(NpgsqlDataReader reader)
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

        return new WebEnrollmentResponseDto(
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
            _options.InstanceId);
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
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE student
            SET active_count = GREATEST(0, active_count + @countDelta),
                active_ects = GREATEST(0.0, active_ects + @ectsDelta)
            WHERE id = @studentId;", conn, tx);
        cmd.Parameters.AddWithValue("countDelta", countDelta);
        cmd.Parameters.AddWithValue("ectsDelta", ectsDelta);
        cmd.Parameters.AddWithValue("studentId", studentId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasActiveEnrollmentForDisciplineAsync(
        string studentId,
        string disciplineId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT EXISTS(
                SELECT 1
                FROM enrollment
                WHERE student_id = @studentId
                  AND discipline_id = @disciplineId
                  AND status IN ('held', 'confirmed')
                UNION ALL
                SELECT 1
                FROM decanate_roster
                WHERE student_id = @studentId
                  AND discipline_id = @disciplineId
            );", conn, tx);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("disciplineId", disciplineId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task<(string GroupId, decimal Ects, string Status, DateTimeOffset ExpiresAt)?> GetEnrollmentBasicInfoAsync(
        Guid id,
        string studentId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT e.group_id, d.ects, e.status, e.expires_at
            FROM enrollment e
            JOIN discipline d ON d.id = e.discipline_id
            WHERE e.id = @id AND e.student_id = @studentId;", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("studentId", studentId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetString(0),
            reader.GetDecimal(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async Task<int> ExpireStaleHeldEnrollmentsAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new List<(Guid Id, string StudentId, string GroupId)>();
        await using (var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken))
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT id, student_id, group_id
                FROM enrollment
                WHERE status = 'held' AND expires_at < NOW()
                LIMIT 50;", conn);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        int expiredCount = 0;
        foreach (var (candId, studentId, groupId) in candidates)
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);
            try
            {
                // Канонічний порядок блокувань: student -> discipline_group -> enrollment
                await using (var lockStudentCmd = new NpgsqlCommand(@"
                    SELECT id FROM student WHERE id = @studentId FOR UPDATE;", conn, tx))
                {
                    lockStudentCmd.Parameters.AddWithValue("studentId", studentId);
                    await lockStudentCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var lockGroupCmd = new NpgsqlCommand(@"
                    SELECT id FROM discipline_group WHERE id = @groupId FOR UPDATE;", conn, tx))
                {
                    lockGroupCmd.Parameters.AddWithValue("groupId", groupId);
                    await lockGroupCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                decimal ects = 0;
                bool isEligible = false;
                await using (var lockEnrollCmd = new NpgsqlCommand(@"
                    SELECT e.status, e.expires_at, d.ects
                    FROM enrollment e
                    JOIN discipline d ON d.id = e.discipline_id
                    WHERE e.id = @id
                    FOR UPDATE OF e;", conn, tx))
                {
                    lockEnrollCmd.Parameters.AddWithValue("id", candId);
                    await using var reader = await lockEnrollCmd.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        string status = reader.GetString(0);
                        var exp = reader.GetFieldValue<DateTimeOffset>(1);
                        ects = reader.GetDecimal(2);
                        if (status == "held" && exp < DateTimeOffset.UtcNow)
                        {
                            isEligible = true;
                        }
                    }
                }

                if (isEligible)
                {
                    await using var updEnrollCmd = new NpgsqlCommand(@"
                        UPDATE enrollment
                        SET status = 'expired', version = version + 1
                        WHERE id = @id;", conn, tx);
                    updEnrollCmd.Parameters.AddWithValue("id", candId);
                    await updEnrollCmd.ExecuteNonQueryAsync(cancellationToken);

                    await using var updGroupCmd = new NpgsqlCommand(@"
                        UPDATE discipline_group
                        SET available = LEAST(seats, available + 1)
                        WHERE id = @groupId;", conn, tx);
                    updGroupCmd.Parameters.AddWithValue("groupId", groupId);
                    await updGroupCmd.ExecuteNonQueryAsync(cancellationToken);

                    await using var updStudentCmd = new NpgsqlCommand(@"
                        UPDATE student
                        SET active_count = GREATEST(0, active_count - 1),
                            active_ects = GREATEST(0.0, active_ects - @ects)
                        WHERE id = @studentId;", conn, tx);
                    updStudentCmd.Parameters.AddWithValue("ects", ects);
                    updStudentCmd.Parameters.AddWithValue("studentId", studentId);
                    await updStudentCmd.ExecuteNonQueryAsync(cancellationToken);

                    await tx.CommitAsync(cancellationToken);
                    expiredCount++;
                }
                else
                {
                    await tx.RollbackAsync(cancellationToken);
                }
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
            }
        }

        return expiredCount;
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
            items.Add(MapEnrollment(reader));
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

        return MapEnrollment(reader);
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

        return MapEnrollment(reader);
    }

    public async Task<(WebEnrollmentResponseDto Enrollment, string? RequestPayloadHash, string StudentId)?> GetEnrollmentByIdempotencyKeyAsync(
        string idempotencyKey,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT e.id, e.student_id, e.group_id, e.discipline_id, d.title, d.ects,
                   e.status, e.version, e.created_at, e.expires_at, g.university_tz,
                   e.request_payload_hash
            FROM enrollment e
            JOIN discipline d ON d.id = e.discipline_id
            JOIN discipline_group g ON g.id = e.group_id
            WHERE e.idempotency_key = @idempotencyKey;", conn, tx);
        cmd.Parameters.AddWithValue("idempotencyKey", idempotencyKey);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var dto = MapEnrollment(reader);
        string? hash = reader.IsDBNull(11) ? null : reader.GetString(11);
        string sId = reader.GetString(1);
        return (dto, hash, sId);
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
        string? idempotencyKey,
        string? requestPayloadHash,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO enrollment (
                id, student_id, group_id, discipline_id, status, version, created_at, expires_at,
                idempotency_key, request_payload_hash
            ) VALUES (
                @id, @studentId, @groupId, @disciplineId, @status, @version, @createdAt, @expiresAt,
                @idempotencyKey, @payloadHash
            );", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("groupId", groupId);
        cmd.Parameters.AddWithValue("disciplineId", disciplineId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("createdAt", createdAt);
        cmd.Parameters.AddWithValue("expiresAt", expiresAt);
        cmd.Parameters.Add(new NpgsqlParameter("idempotencyKey", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)idempotencyKey ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("payloadHash", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)requestPayloadHash ?? DBNull.Value
        });

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

    public async Task<bool> ConfirmEnrollmentHeldAsync(
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

        int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<(string StudentId, string GroupId, string DisciplineId, string Status, DateTimeOffset ExpiresAt)?> GetEnrollmentForJointVerificationAsync(
        Guid id,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT student_id, group_id, discipline_id, status, expires_at
            FROM enrollment
            WHERE id = @id
            FOR UPDATE OF enrollment;", conn, tx);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task ResetEnrollmentsAndStudentsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            DELETE FROM enrollment;
            UPDATE student SET active_count = 0, active_ects = 0.0 WHERE id NOT IN ('student-ects-20', 'student-limit-max');
            UPDATE student SET active_count = 2, active_ects = 20.0 WHERE id = 'student-ects-20';
            UPDATE student SET active_count = 5, active_ects = 25.0 WHERE id = 'student-limit-max';", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
