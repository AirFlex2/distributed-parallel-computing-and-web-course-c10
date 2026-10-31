using Npgsql;

namespace EnrollmentService.Repositories;

public interface IEnrollmentRequestRepository
{
    Task<EnrollmentAcceptedResponseDto?> GetAcceptedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<EnrollmentStatusResponseDto?> MarkFirstReadAndGetStatusAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<EnrollmentStatusResponseDto?> GetStatusByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task InsertQueuedRequestAsync(
        QueuedRequestItem item,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default);

    Task DeleteRequestAsync(
        Guid id,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default);

    Task MarkStartedAsync(
        Guid id,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid id,
        string status,
        string? rejectionReason,
        string? assignedGroupId,
        DateTimeOffset finishedAt,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx = null,
        CancellationToken cancellationToken = default);

    Task UpsertSecondChoiceRequestAsync(
        Guid id,
        string studentId,
        string primaryGroupId,
        string secondChoiceGroupId,
        DateTimeOffset now,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(Guid RequestId, string StudentId, string SecondChoiceGroupId)>> GetSecondChoiceCandidatesAsync(
        string primaryGroupId,
        Guid? filterRequestId,
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default);

    Task ResetRequestsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default);
}
