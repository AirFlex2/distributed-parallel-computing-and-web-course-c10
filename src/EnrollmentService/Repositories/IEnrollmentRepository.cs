using EnrollmentService.Domain;
using Npgsql;

namespace EnrollmentService.Repositories;

public interface IEnrollmentRepository
{
    Task<IReadOnlyList<StudentEntity>> GetStudentsAsync(
        CancellationToken cancellationToken = default);

    Task<StudentEntity?> GetStudentByIdAsync(
        string studentId,
        CancellationToken cancellationToken = default);

    Task<StudentEntity?> GetStudentForUpdateAsync(
        string studentId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task UpdateStudentStatsAsync(
        string studentId,
        int countDelta,
        decimal ectsDelta,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task<bool> HasActiveEnrollmentForDisciplineAsync(
        string studentId,
        string disciplineId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(Guid Id, string GroupId)>> GetActiveEnrollmentsForDisciplineAsync(
        string studentId,
        string disciplineId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WebEnrollmentResponseDto>> GetStudentEnrollmentsCursorAsync(
        string studentId,
        int limit,
        DateTimeOffset? cursorCreatedAt,
        Guid? cursorId,
        CancellationToken cancellationToken = default);

    Task<WebEnrollmentResponseDto?> GetEnrollmentByIdAsync(
        Guid id,
        string studentId,
        CancellationToken cancellationToken = default);

    Task<(string StudentId, string GroupId)?> GetEnrollmentLookupAsync(
        Guid id,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task<WebEnrollmentResponseDto?> GetEnrollmentForUpdateAsync(
        Guid id,
        string studentId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task CreateEnrollmentAsync(
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
        CancellationToken cancellationToken = default);

    Task CancelEnrollmentAsync(
        Guid id,
        int newVersion,
        DateTimeOffset now,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task ConfirmEnrollmentHeldAsync(
        Guid id,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default);

    Task ResetEnrollmentsAndStudentsAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken = default);
}
