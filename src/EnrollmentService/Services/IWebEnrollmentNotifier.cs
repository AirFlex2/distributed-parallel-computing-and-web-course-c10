namespace EnrollmentService.Services;

/// <summary>
/// Інтерфейс повідомлення Web-контуру про внесення запису до відомості деканату DPC.
/// Відокремлює доменну зону відповідальності DPC (ведення відомості decanate_roster)
/// від доменної зони Web (переведення статусу held -> confirmed у власній транзакції).
/// </summary>
public interface IWebEnrollmentNotifier
{
    /// <summary>
    /// Повідомляє Web-контур про те, що студент був успішно внесений до відомості деканату,
    /// і Web повинен самостійно підтвердити резервування (held -> confirmed).
    /// Метод є строго ідемпотентним: повторні виклики не призводять до зміни лічильників чи помилок.
    /// </summary>
    Task<bool> NotifyRosterRecordedAsync(
        Guid enrollmentId,
        Guid requestId,
        CancellationToken cancellationToken = default);
}
