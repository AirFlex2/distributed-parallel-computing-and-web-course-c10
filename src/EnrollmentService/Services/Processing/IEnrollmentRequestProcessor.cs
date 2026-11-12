using EnrollmentService.Queues;

namespace EnrollmentService.Services.Processing;

public interface IEnrollmentRequestProcessor
{
    Task ProcessItemAsync(
        int workerId,
        QueuedRequestItem item,
        bool isAsyncModel,
        IBoundedRequestQueue queue,
        CancellationToken cancellationToken = default);
}
