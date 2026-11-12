using EnrollmentService.Repositories;

namespace EnrollmentService.Services;

public sealed class HeldEnrollmentExpirationBackgroundService : BackgroundService
{
    private readonly EnrollmentRepository _repo;
    private readonly EnrollmentService _enrollmentService;
    private readonly ServiceOptions _options;

    public HeldEnrollmentExpirationBackgroundService(
        EnrollmentRepository repo,
        EnrollmentService enrollmentService,
        ServiceOptions options)
    {
        _repo = repo;
        _enrollmentService = enrollmentService;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Періодичне узгодження (reconciliation) Web-записів held, які вже внесені до decanate_roster
        // 2. Періодичне звільнення прострочених held-записів (15 хв) у фоновому таймері
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await _enrollmentService.ReconcilePendingRosterConfirmationsAsync(stoppingToken);
                await _repo.ExpireStaleHeldEnrollmentsAsync(stoppingToken);
                await _enrollmentService.CleanupExpiredIdempotencyKeysAsync(
                    _options.IdempotencyCleanupBatchSize,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "held_expiration_background_error", new Dictionary<string, object?>
                {
                    ["error"] = ex.Message
                });
            }
        }
    }
}
