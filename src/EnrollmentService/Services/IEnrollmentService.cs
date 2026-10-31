using EnrollmentService.Domain;

namespace EnrollmentService.Services;

public interface IEnrollmentService
{
    Task<ServiceResult<WebEnrollmentResponseDto>> CreateEnrollmentAsync(
        string studentId,
        CreateWebEnrollmentDto dto,
        string? simulatedNowHeader,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<WebEnrollmentListResponseDto>> GetEnrollmentsAsync(
        string studentId,
        int limit,
        string? cursorRaw,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<WebEnrollmentResponseDto>> GetEnrollmentByIdAsync(
        string studentId,
        string enrollmentIdRaw,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<WebEnrollmentResponseDto>> CancelEnrollmentAsync(
        string studentId,
        string enrollmentIdRaw,
        CancellationToken cancellationToken = default);

    Task<ServiceResult<WebDisciplineListResponseDto>> GetDisciplinesAsync(
        int limit,
        string? afterId,
        bool includeTest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WebStudentDto>> GetStudentsAsync(
        CancellationToken cancellationToken = default);
}
