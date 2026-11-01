using EnrollmentService.Repositories;

namespace EnrollmentService.Services;

public sealed class HeldEnrollmentExpirationBackgroundService : BackgroundService
{
    private readonly EnrollmentRepository _repo;
    private readonly ServiceOptions _options;

    public HeldEnrollmentExpirationBackgroundService(EnrollmentRepository repo, ServiceOptions options)
    {
        _repo = repo;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Періодичне звільнення прострочених held-записів (15 хв) у фоновому таймері
        // без створення навантаження на гарячі GET-маршрути списків
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await _repo.ExpireStaleHeldEnrollmentsAsync(stoppingToken);
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
