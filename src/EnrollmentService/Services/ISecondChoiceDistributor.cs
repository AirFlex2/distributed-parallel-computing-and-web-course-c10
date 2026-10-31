using EnrollmentService.Domain;

namespace EnrollmentService.Services;

public interface ISecondChoiceDistributor
{
    Task<ServiceResult<SecondChoiceDistributionResponseDto>> DistributeSecondChoiceAsync(
        string primaryGroupId,
        SecondChoiceDistributionRequestDto? request,
        CancellationToken cancellationToken = default);
}
