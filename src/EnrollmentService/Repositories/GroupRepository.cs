using EnrollmentService.Domain;
using Npgsql;

namespace EnrollmentService.Repositories;

public sealed class GroupRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public GroupRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<DisciplineGroupEntity>> GetGroupsByDisciplineIdsAsync(
        IReadOnlyCollection<string> disciplineIds,
        CancellationToken cancellationToken = default)
    {
        if (disciplineIds.Count == 0)
        {
            return Array.Empty<DisciplineGroupEntity>();
        }

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT g.id, g.discipline_id, g.group_code, g.seats, g.available,
                   g.opens_at, g.closes_at, g.university_tz, g.closed, d.ects
            FROM discipline_group g
            JOIN discipline d ON d.id = g.discipline_id
            WHERE g.discipline_id = ANY(@ids)
            ORDER BY g.discipline_id ASC, g.id ASC;", conn);
        cmd.Parameters.AddWithValue("ids", disciplineIds.ToArray());

        var groups = new List<DisciplineGroupEntity>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new DisciplineGroupEntity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                Math.Max(0, reader.GetInt32(4)),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetString(7),
                reader.GetBoolean(8),
                reader.GetDecimal(9)));
        }

        return groups;
    }

    public async Task<IReadOnlyList<DisciplineEntity>> GetDisciplinesAsync(
        int limit,
        string? afterId,
        bool includeTest,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        bool hasAfter = !string.IsNullOrWhiteSpace(afterId);
        string discSql = hasAfter
            ? @"
                SELECT id, code, title, ects
                FROM discipline
                WHERE (@includeTest OR is_test_only = FALSE) AND id > @after
                ORDER BY id ASC
                LIMIT @fetchLimit;"
            : @"
                SELECT id, code, title, ects
                FROM discipline
                WHERE (@includeTest OR is_test_only = FALSE)
                ORDER BY id ASC
                LIMIT @fetchLimit;";

        var disciplines = new List<DisciplineEntity>();
        await using var discCmd = new NpgsqlCommand(discSql, conn);
        discCmd.Parameters.AddWithValue("includeTest", includeTest);
        if (hasAfter)
        {
            discCmd.Parameters.AddWithValue("after", afterId!.Trim());
        }
        discCmd.Parameters.AddWithValue("fetchLimit", limit + 1);

        await using var reader = await discCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            disciplines.Add(new DisciplineEntity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDecimal(3)));
        }

        return disciplines;
    }

    public async Task<bool> DisciplineExistsAsync(string disciplineId, CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM discipline WHERE id = @id);", conn);
        cmd.Parameters.AddWithValue("id", disciplineId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task<DisciplineGroupEntity?> GetGroupByIdAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT g.id, g.discipline_id, g.group_code, g.seats, g.available, g.opens_at, g.closes_at, g.university_tz, g.closed, d.ects
            FROM discipline_group g
            JOIN discipline d ON d.id = g.discipline_id
            WHERE g.id = @groupId;", conn);
        cmd.Parameters.AddWithValue("groupId", groupId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DisciplineGroupEntity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            Math.Max(0, reader.GetInt32(4)),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7),
            reader.GetBoolean(8),
            reader.GetDecimal(9));
    }

    public async Task<(DisciplineGroupEntity Group, string DisciplineTitle, decimal DisciplineEcts)?> GetGroupWithDisciplineAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        bool forUpdate = false,
        CancellationToken cancellationToken = default)
    {
        string sql = @"
            SELECT g.id, g.discipline_id, g.group_code, g.seats, g.available, g.opens_at, g.closes_at, g.university_tz, g.closed,
                   d.title, d.ects
            FROM discipline_group g
            JOIN discipline d ON d.id = g.discipline_id
            WHERE g.id = @groupId" + (forUpdate ? " FOR UPDATE OF g;" : ";");
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("groupId", groupId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var group = new DisciplineGroupEntity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7),
            reader.GetBoolean(8));

        string title = reader.GetString(9);
        decimal ects = reader.GetDecimal(10);

        return (group, title, ects);
    }

    public async Task<(DisciplineGroupEntity Group, string DisciplineTitle, decimal DisciplineEcts)?> GetGroupWithDisciplineForUpdateAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        return await GetGroupWithDisciplineAsync(groupId, conn, tx, forUpdate: true, cancellationToken);
    }

    public async Task<DisciplineGroupEntity?> GetGroupForUpdateAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, discipline_id, group_code, seats, available, opens_at, closes_at, university_tz, closed
            FROM discipline_group
            WHERE id = @groupId
            FOR UPDATE;", conn, tx);
        cmd.Parameters.AddWithValue("groupId", groupId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DisciplineGroupEntity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7),
            reader.GetBoolean(8));
    }

    public async Task<bool> DecrementAvailableAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE discipline_group
            SET available = available - 1
            WHERE id = @groupId AND available > 0;", conn, tx);
        cmd.Parameters.AddWithValue("groupId", groupId);

        int affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task SetAvailableUnsafeAsync(
        string groupId,
        int newAvailable,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE discipline_group
            SET available = @available
            WHERE id = @groupId;", conn, tx);
        cmd.Parameters.AddWithValue("groupId", groupId);
        cmd.Parameters.AddWithValue("available", newAvailable);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task IncrementAvailableAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE discipline_group
            SET available = LEAST(seats, available + 1)
            WHERE id = @groupId;", conn, tx);
        cmd.Parameters.AddWithValue("groupId", groupId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DisciplineGroupEntity?> CloseGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE discipline_group
            SET closed = TRUE
            WHERE id = @id
            RETURNING id, discipline_id, group_code, seats, available, opens_at, closes_at, university_tz, closed;", conn);
        cmd.Parameters.AddWithValue("id", groupId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DisciplineGroupEntity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            Math.Max(0, reader.GetInt32(4)),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7),
            reader.GetBoolean(8));
    }

    public async Task<GroupSeatsResponseDto?> GetGroupSeatsByIdAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT group_id, discipline_title, seats, taken, roster_count
            FROM group_seats
            WHERE group_id = @id;", conn);
        cmd.Parameters.AddWithValue("id", groupId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        int seats = reader.GetInt32(2);
        int taken = reader.GetInt32(3);
        int rosterCount = reader.GetInt32(4);

        return new GroupSeatsResponseDto(
            reader.GetString(0),
            reader.GetString(1),
            seats,
            taken,
            Math.Max(0, seats - taken),
            taken <= seats && rosterCount <= seats,
            rosterCount);
    }

    public async Task<IReadOnlyList<GroupSeatsResponseDto>> GetAllGroupSeatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var list = new List<GroupSeatsResponseDto>();

        await using var cmd = new NpgsqlCommand(@"
            SELECT group_id, discipline_title, seats, taken, roster_count
            FROM group_seats
            ORDER BY group_id;", conn);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int seats = reader.GetInt32(2);
            int taken = reader.GetInt32(3);
            int rosterCount = reader.GetInt32(4);

            list.Add(new GroupSeatsResponseDto(
                reader.GetString(0),
                reader.GetString(1),
                seats,
                taken,
                Math.Max(0, seats - taken),
                taken <= seats && rosterCount <= seats,
                rosterCount));
        }

        return list;
    }

    public async Task ResetGroupsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE discipline_group SET available = seats, closed = FALSE;", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
