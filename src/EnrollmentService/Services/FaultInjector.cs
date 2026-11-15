using System.Diagnostics;

namespace EnrollmentService.Services;

public sealed class SimulatedRayWorkerCrashException : Exception
{
    public SimulatedRayWorkerCrashException(string message) : base(message) { }
}

// Web О2.3 та РПО О3.4: Контрольовані відмови (/faults).
// - after_commit_before_response: після COMMIT створення Web-запису, до HTTP-відповіді.
// - after_effect_before_ack: після застосування ефекту приймачем у БД, до ack завершення задачі (для тестів).
public static class FaultInjector
{
    public const string AfterCommitBeforeResponseFaultName = "after_commit_before_response";
    public const string AfterEffectBeforeAckFaultName = "after_effect_before_ack";

    // Тестові перехоплювачі виключно для xUnit-тестів в одному процесі (щоб не вбивати процес тест-раннера).
    public static Action<string, Guid, string, string?>? InProcessTestFaultIntercept { get; set; }
    public static Action<string, Guid, string>? InProcessAfterEffectFaultIntercept { get; set; }

    public static void MaybeTriggerAfterCommitBeforeResponse(
        ServiceOptions options,
        Guid enrollmentId,
        string studentId,
        string? idempotencyKey)
    {
        if (!TryClaimFaultFlag(options, AfterCommitBeforeResponseFaultName))
        {
            return;
        }

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

        Environment.Exit(137);
        Environment.FailFast("Controlled fault: after_commit_before_response");
    }

    public static void MaybeTriggerAfterEffectBeforeAckInProcess(
        ServiceOptions options,
        Guid requestId,
        string studentId)
    {
        if (!TryClaimFaultFlag(options, AfterEffectBeforeAckFaultName))
        {
            return;
        }

        StructuredJsonLogger.LogEvent(options.InstanceId, "fault.hit", new Dictionary<string, object?>
        {
            ["fault"] = AfterEffectBeforeAckFaultName,
            ["request_id"] = requestId.ToString("D"),
            ["student_id"] = studentId
        });

        var intercept = InProcessAfterEffectFaultIntercept;
        if (intercept != null)
        {
            intercept(options.InstanceId, requestId, studentId);
        }

        throw new SimulatedRayWorkerCrashException(
            $"Simulated Ray worker crash ({AfterEffectBeforeAckFaultName}) after receiver committed effect for request {requestId}.");
    }

    private static bool TryClaimFaultFlag(ServiceOptions options, string faultName)
    {
        if (!options.FaultsEnabled)
        {
            return false;
        }

        string dir = string.IsNullOrWhiteSpace(options.FaultsDirectory) ? "/faults" : options.FaultsDirectory;
        string flagPath = Path.Combine(dir, faultName);

        if (!File.Exists(flagPath))
        {
            return false;
        }

        string claimedPath = Path.Combine(dir, $".claimed_{faultName}_{Guid.NewGuid():N}");
        try
        {
            File.Move(flagPath, claimedPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            File.Delete(claimedPath);
        }
        catch
        {
        }

        return true;
    }
}
