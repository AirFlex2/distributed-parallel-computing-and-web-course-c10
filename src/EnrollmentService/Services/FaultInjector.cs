using System.Diagnostics;

namespace EnrollmentService.Services;

// Web О2.3: Контрольована відмова (failpoint) після фіксації транзакції в PostgreSQL (COMMIT),
// але до відправлення HTTP-відповіді клієнту.
// Активується лише за наявності FAULTS=on та файлу-прапорця /faults/after_commit_before_response.
public static class FaultInjector
{
    public const string AfterCommitBeforeResponseFaultName = "after_commit_before_response";

    // Тестовий перехоплювач виключно для xUnit-тестів в одному процесі (щоб не вбивати процес тест-раннера).
    // У реальному контейнері (коли перехоплювач == null) виконується негайне аварійне завершення процесу (SIGKILL).
    public static Action<string, Guid, string, string?>? InProcessTestFaultIntercept { get; set; }

    public static void MaybeTriggerAfterCommitBeforeResponse(
        ServiceOptions options,
        Guid enrollmentId,
        string studentId,
        string? idempotencyKey)
    {
        if (!options.FaultsEnabled)
        {
            return;
        }

        string dir = string.IsNullOrWhiteSpace(options.FaultsDirectory) ? "/faults" : options.FaultsDirectory;
        string flagPath = Path.Combine(dir, AfterCommitBeforeResponseFaultName);

        if (!File.Exists(flagPath))
        {
            return;
        }

        // Атомарне захоплення прапорця через перейменування в тій самій директорії,
        // щоб прапорець спрацював рівно один раз навіть при одночасних запитах або спільному томі.
        string claimedPath = Path.Combine(dir, $".claimed_{AfterCommitBeforeResponseFaultName}_{Guid.NewGuid():N}");
        try
        {
            File.Move(flagPath, claimedPath);
        }
        catch ( IOException )
        {
            // Інший потік або примірник уже захопив/видалив прапорець
            return;
        }
        catch ( UnauthorizedAccessException )
        {
            return;
        }

        try
        {
            File.Delete(claimedPath);
        }
        catch
        {
            // Ігноруємо помилку видалення тимчасового файлу після атомарного перейменування
        }

        // Обов'язковий структурований запис у журнал перед аварійним завершенням (О2.3 / Д1.4)
        StructuredJsonLogger.LogEvent(options.InstanceId, "fault.hit", new Dictionary<string, object?>
        {
            ["fault"] = AfterCommitBeforeResponseFaultName,
            ["enrollment_id"] = enrollmentId.ToString("D"),
            ["student_id"] = studentId,
            ["idempotency_key"] = idempotencyKey
        });

        var intercept = InProcessTestFaultIntercept;
        if (intercept != null)
        {
            intercept(options.InstanceId, enrollmentId, studentId, idempotencyKey);
            return;
        }

        // Негайне неграціозне завершення процесу (включно з PID 1 у Linux-контейнері Docker)
        // без закриття HTTP-відповіді Kestrel (обрив TCP-з'єднання після COMMIT).
        Environment.Exit(137);
        Environment.FailFast("Controlled fault: after_commit_before_response");
    }
}
