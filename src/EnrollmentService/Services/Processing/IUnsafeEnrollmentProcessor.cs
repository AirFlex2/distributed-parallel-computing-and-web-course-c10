using EnrollmentService.Metrics;
using EnrollmentService.Repositories;

namespace EnrollmentService.Services.Processing;

public interface IUnsafeEnrollmentProcessor
{
    Task<(string Status, string? Reason)> ProcessUnsafeAsync(
        QueuedRequestItem item,
        CancellationToken cancellationToken = default);
}

public sealed class UnsafeEnrollmentProcessor : IUnsafeEnrollmentProcessor
{
    private readonly IGroupRepository _groupRepo;
    private readonly IEnrollmentRequestRepository _requestRepo;
    private readonly IDecanateRepository _decanateRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ISafeEnrollmentProcessor _safeProcessor;
    private readonly MetricsCollector _metrics;

    public UnsafeEnrollmentProcessor(
        IGroupRepository groupRepo,
        IEnrollmentRequestRepository requestRepo,
        IDecanateRepository decanateRepo,
        IDbConnectionFactory connectionFactory,
        ISafeEnrollmentProcessor safeProcessor,
        MetricsCollector metrics)
    {
        _groupRepo = groupRepo;
        _requestRepo = requestRepo;
        _decanateRepo = decanateRepo;
        _connectionFactory = connectionFactory;
        _safeProcessor = safeProcessor;
        _metrics = metrics;
    }

    public async Task<(string Status, string? Reason)> ProcessUnsafeAsync(
        QueuedRequestItem item,
        CancellationToken cancellationToken = default)
    {
        var group = await _groupRepo.GetGroupByIdAsync(item.GroupId, cancellationToken);
        if (group == null)
        {
            string notFoundReason = $"Навчальну групу '{item.GroupId}' не знайдено.";
            var finishedAt = _metrics.MarkProcessingFinished(item);
            await using var c = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await _requestRepo.UpdateStatusAsync(item.Id, "failed", notFoundReason, null, finishedAt, c, null, cancellationToken);
            return ("failed", notFoundReason);
        }

        // Захист реальних академічних даних (§1.9): небезпечний режим виконується лише на окремих тестових групах
        if (!group.AllowOvercommitUnsafe)
        {
            return await _safeProcessor.ProcessSafeAsync(item, cancellationToken);
        }

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        int currentRosterCount = await _decanateRepo.GetGroupRosterCountAsync(item.GroupId, conn, null, cancellationToken);

        if (!group.Closed && currentRosterCount < group.Seats)
        {
            // Вікно вразливості TOCTOU між перевіркою заповненості відомості деканату та записом без блокування
            await Task.Delay(600, cancellationToken);

            await _decanateRepo.InsertRosterEntryAsync(
                item.Id,
                item.StudentId,
                item.GroupId,
                group.DisciplineId,
                group.DisciplineEcts > 0 ? group.DisciplineEcts : 3.0m,
                null,
                "primary_roster",
                DateTimeOffset.UtcNow,
                conn,
                null,
                cancellationToken);

            var finishedAtUtc = _metrics.MarkProcessingFinished(item);

            await _requestRepo.UpdateStatusAsync(
                item.Id,
                "done",
                null,
                item.GroupId,
                finishedAtUtc,
                conn,
                null,
                cancellationToken);

            return ("done", null);
        }
        else
        {
            const string reason = "Вичерпано ліміт місць у відомості деканату (небезпечний режим).";
            var finishedAtUtc = _metrics.MarkProcessingFinished(item);

            await _requestRepo.UpdateStatusAsync(
                item.Id,
                "rejected",
                reason,
                null,
                finishedAtUtc,
                conn,
                null,
                cancellationToken);

            return ("rejected", reason);
        }
    }
}
