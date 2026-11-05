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
        // Механізм повторної доставки (Retry): до 3 спроб з експоненційною затримкою (50 мс, 100 мс, 200 мс)
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                // Web самостійно виконує перехід held -> confirmed у власній транзакції
                var result = await _enrollmentService.ConfirmEnrollmentDetailedAsync(enrollmentId, requestId, cancellationToken);
                switch (result)
                {
                    case RosterConfirmationResult.Success:
                    case RosterConfirmationResult.AlreadyConfirmed:
                        return true;

                    case RosterConfirmationResult.PermanentFailure:
                        // Постійне порушення умов (запис не знайдено, або статус скасований чи прострочений) —
                        // повторні спроби беззмістовні; реєструємо подію та повертаємо false без затримок.
                        StructuredJsonLogger.LogEvent("web_notifier", "roster_confirmation_permanent_failure", new Dictionary<string, object?>
                        {
                            ["attempt"] = attempt,
                            ["enrollment_id"] = enrollmentId,
                            ["request_id"] = requestId
                        });
                        return false;

                    case RosterConfirmationResult.TransientFailure:
                    default:
                        // Тимчасова неможливість підтвердження — повторюємо з експоненційною затримкою
                        if (attempt < 3 && !cancellationToken.IsCancellationRequested)
                        {
                            int delayMs = 50 * (1 << (attempt - 1)); // 50, 100, 200 мс
                            await Task.Delay(delayMs, cancellationToken);
                        }
                        break;
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
                int delayMs = 50 * (1 << (attempt - 1)); // 50, 100, 200 мс
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        return false;
    }
}
