using Npgsql;

namespace EnrollmentService.Repositories;

public interface IDecanateRepository
{
    Task<string?> GetRosterGroupForRequestAsync(
        Guid requestId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default);

    Task<(string GroupId, Guid? EnrollmentId)?> GetRosterAssignmentForRequestAsync(
        Guid requestId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default);

    Task<(bool AlreadyInRoster, int OtherDisciplineCount, decimal OtherEcts)> GetStudentRosterStatsAsync(
        string studentId,
        string disciplineId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default);

    Task<int> GetGroupRosterCountAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default);

    Task InsertRosterEntryAsync(
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
        CancellationToken cancellationToken = default);

    Task DeleteRosterEntryForEnrollmentAsync(
        Guid enrollmentId,
        string studentId,
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DecanateRosterEntryDto>> GetRosterEntriesAsync(
        string? groupId,
        string? studentId,
        Guid? requestId,
        CancellationToken cancellationToken = default);

    Task ResetRosterAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default);
}
