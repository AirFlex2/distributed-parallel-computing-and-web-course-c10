using EnrollmentService.Domain;
using Npgsql;

namespace EnrollmentService.Repositories;

public interface IGroupRepository
{
    Task<IReadOnlyList<DisciplineEntity>> GetDisciplinesAsync(
        int limit,
        string? afterId,
        bool includeTest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DisciplineGroupEntity>> GetAllDisciplineGroupsAsync(
        CancellationToken cancellationToken = default);

    Task<DisciplineGroupEntity?> GetGroupByIdAsync(
        string groupId,
        CancellationToken cancellationToken = default);

    Task<(DisciplineGroupEntity Group, string DisciplineTitle, decimal DisciplineEcts)?> GetGroupWithDisciplineForUpdateAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task<bool> DecrementAvailableAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task IncrementAvailableAsync(
        string groupId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task<DisciplineGroupEntity?> CloseGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default);

    Task<GroupSeatsResponseDto?> GetGroupSeatsByIdAsync(
        string groupId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GroupSeatsResponseDto>> GetAllGroupSeatsAsync(
        CancellationToken cancellationToken = default);

    Task ResetGroupsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default);
}
