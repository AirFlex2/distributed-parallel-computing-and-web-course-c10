using EnrollmentService.Domain;
using Npgsql;

namespace EnrollmentService.Repositories;

public sealed class GroupRepository : IGroupRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public GroupRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
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

    public async Task<IReadOnlyList<DisciplineGroupEntity>> GetAllDisciplineGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var groups = new List<DisciplineGroupEntity>();

        await using var grpCmd = new NpgsqlCommand(@"
            SELECT id, discipline_id, group_code, seats, available, opens_at, closes_at, university_tz, closed, allow_overcommit_unsafe
            FROM discipline_group
            ORDER BY id ASC;", conn);

        await using var grpReader = await grpCmd.ExecuteReaderAsync(cancellationToken);
        while (await grpReader.ReadAsync(cancellationToken))
        {
            groups.Add(new DisciplineGroupEntity(
                grpReader.GetString(0),
                grpReader.GetString(1),
                grpReader.GetString(2),
                grpReader.GetInt32(3),
                Math.Max(0, grpReader.GetInt32(4)),
                grpReader.GetFieldValue<DateTimeOffset>(5),
                grpReader.GetFieldValue<DateTimeOffset>(6),
                grpReader.GetString(7),
                grpReader.GetBoolean(8),
                grpReader.GetBoolean(9)));
        }

        return groups;
    }

    public async Task<DisciplineGroupEntity?> GetGroupByIdAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT g.id, g.discipline_id, g.group_code, g.seats, g.available, g.opens_at, g.closes_at, g.university_tz, g.closed, g.allow_overcommit_unsafe, d.ects
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
            reader.GetBoolean(9),
            reader.GetDecimal(10));
    }

    public async Task<(DisciplineGroupEntity Group, string DisciplineTitle, decimal DisciplineEcts)?> GetGroupWithDisciplineForUpdateAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT g.id, g.discipline_id, g.group_code, g.seats, g.available, g.opens_at, g.closes_at, g.university_tz, g.closed, g.allow_overcommit_unsafe,
                   d.title, d.ects
            FROM discipline_group g
            JOIN discipline d ON d.id = g.discipline_id
            WHERE g.id = @groupId
            FOR UPDATE OF g;", conn, tx);
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
            reader.GetBoolean(8),
            reader.GetBoolean(9));

        string title = reader.GetString(10);
        decimal ects = reader.GetDecimal(11);

        return (group, title, ects);
    }

    public async Task<bool> DecrementAvailableAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
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

    public async Task IncrementAvailableAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
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
            RETURNING id, discipline_id, group_code, seats, available, opens_at, closes_at, university_tz, closed, allow_overcommit_unsafe;", conn);
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
            reader.GetBoolean(8),
            reader.GetBoolean(9));
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
            taken <= seats,
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
                taken <= seats,
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
