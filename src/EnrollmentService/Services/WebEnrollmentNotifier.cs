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
        // Web самостійно виконує перехід held -> confirmed у власній транзакції
        return await _enrollmentService.ConfirmEnrollmentFromRosterAsync(enrollmentId, requestId, cancellationToken);
    }
}
