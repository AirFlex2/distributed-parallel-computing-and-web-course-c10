using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;

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

public sealed class EnrollmentRequestProcessor : IEnrollmentRequestProcessor
{
    private readonly ISlowOperationSimulator _slowSimulator;
    private readonly ISafeEnrollmentProcessor _safeProcessor;
    private readonly IUnsafeEnrollmentProcessor _unsafeProcessor;
    private readonly IEnrollmentRequestRepository _requestRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly MetricsCollector _metrics;
    private readonly ServiceOptions _options;

    public EnrollmentRequestProcessor(
        ISlowOperationSimulator slowSimulator,
        ISafeEnrollmentProcessor safeProcessor,
        IUnsafeEnrollmentProcessor unsafeProcessor,
        IEnrollmentRequestRepository requestRepo,
        IDbConnectionFactory connectionFactory,
        MetricsCollector metrics,
        ServiceOptions options)
    {
        _slowSimulator = slowSimulator;
        _safeProcessor = safeProcessor;
        _unsafeProcessor = unsafeProcessor;
        _requestRepo = requestRepo;
        _connectionFactory = connectionFactory;
        _metrics = metrics;
        _options = options;
    }

    public async Task ProcessItemAsync(
        int workerId,
        QueuedRequestItem item,
        bool isAsyncModel,
        IBoundedRequestQueue queue,
        CancellationToken cancellationToken = default)
    {
        _metrics.RecordWorkerStarted(item, queue.Count);
        var startedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            await _requestRepo.MarkStartedAsync(item.Id, startedAtUtc, cancellationToken);

            // О1.3 та О1.4: Повільний виклик (200-700 мс) строго поза транзакцією та блокуванням БД
            await _slowSimulator.SimulateAsync(isAsyncModel, cancellationToken);

            string finalStatus;
            if (_options.UnsafeMode)
            {
                (finalStatus, _) = await _unsafeProcessor.ProcessUnsafeAsync(item, cancellationToken);
            }
            else
            {
                (finalStatus, _) = await _safeProcessor.ProcessSafeAsync(item, cancellationToken);
            }

            _metrics.RecordWorkerFinished(item, finalStatus, queue.Count);
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_processing_error", new Dictionary<string, object?>
            {
                ["worker_id"] = workerId,
                ["request_id"] = item.Id.ToString("D"),
                ["error"] = ex.Message
            });

            await MarkRequestFailedAsync(item, ex.Message);
            _metrics.RecordWorkerFinished(item, "failed", queue.Count);
        }
    }

    private async Task MarkRequestFailedAsync(QueuedRequestItem item, string errorMessage)
    {
        try
        {
            var finishedAtUtc = _metrics.MarkProcessingFinished(item);
            await using var conn = await _connectionFactory.OpenConnectionAsync();
            await _requestRepo.UpdateStatusAsync(item.Id, "failed", errorMessage, null, finishedAtUtc, conn);
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "mark_failed_error", new Dictionary<string, object?>
            {
                ["request_id"] = item.Id.ToString("D"),
                ["error"] = ex.Message
            });
        }
    }
}
