namespace EnrollmentService.Services;

/// <summary>
/// Реалізація IWebEnrollmentNotifier, що викликає Web-сервіс для атомарного підтвердження
/// запису у власній транзакції Web-контуру.
/// Забезпечує ідемпотентність: повторний виклик не змінює версію або лічильники, якщо статус вже 'confirmed'.
/// </summary>
public sealed class WebEnrollmentNotifier : IWebEnrollmentNotifier
{
    private readonly EnrollmentService _enrollmentService;

    public WebEnrollmentNotifier(EnrollmentService enrollmentService)
    {
        _enrollmentService = enrollmentService ?? throw new ArgumentNullException(nameof(enrollmentService));
    }

    public async Task<bool> NotifyRosterRecordedAsync(
        Guid enrollmentId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        // Механізм повторної доставки (Retry): до 3 спроб з експоненційною затримкою (50 мс, 100 мс, 150 мс)
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                // Web самостійно виконує перехід held -> confirmed у власній транзакції
                bool ok = await _enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, requestId, cancellationToken);
                if (ok)
                {
                    return true;
                }
            }
            catch (Exception ex) when (attempt < 3 && !cancellationToken.IsCancellationRequested)
            {
                StructuredJsonLogger.LogEvent("web_notifier", "roster_notification_retry", new Dictionary<string, object?>
                {
                    ["attempt"] = attempt,
                    ["enrollment_id"] = enrollmentId,
                    ["request_id"] = requestId,
                    ["error"] = ex.Message
                });
                await Task.Delay(50 * attempt, cancellationToken);
            }
        }

        return false;
    }
}
