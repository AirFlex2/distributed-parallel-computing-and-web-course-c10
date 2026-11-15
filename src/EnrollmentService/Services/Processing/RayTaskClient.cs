using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EnrollmentService.Queues;

namespace EnrollmentService.Services.Processing;

public interface IRayTaskClient
{
    Task<(string Status, string? Reason, bool Committed)> ExecuteAsync(
        QueuedRequestItem item,
        bool isUnsafe,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Клієнт диспетчеризації задач у Ray (РПО ЛР3: О3.1, О3.4).
/// Якщо задано RAY_DISPATCHER_URL, передає задачу на HTTP-диспетчер Ray-кластера (ray-head),
/// де її виконує @ray.remote воркер з автоматичним повтором (max_retries).
/// Якщо RAY_DISPATCHER_URL порожній (автономні тести xUnit без зовнішнього Ray), використовує
/// in-process виконавець із тією ж моделлю ретраїв та Result Receiver (RayEffectReceiverService).
/// </summary>
public sealed class RayTaskClient : IRayTaskClient
{
    private static readonly HttpClient SharedFallbackHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private readonly ServiceOptions _options;
    private readonly RayEffectReceiverService _receiverService;
    private readonly IHttpClientFactory? _httpClientFactory;

    public RayTaskClient(
        ServiceOptions options,
        RayEffectReceiverService receiverService,
        IHttpClientFactory? httpClientFactory = null)
    {
        _options = options;
        _receiverService = receiverService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<(string Status, string? Reason, bool Committed)> ExecuteAsync(
        QueuedRequestItem item,
        bool isUnsafe,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.RayDispatcherUrl))
        {
            return await ExecuteViaRayClusterAsync(item, isUnsafe, cancellationToken);
        }

        return await ExecuteInProcessWithRetryAsync(item, isUnsafe, cancellationToken);
    }

    private async Task<(string Status, string? Reason, bool Committed)> ExecuteViaRayClusterAsync(
        QueuedRequestItem item,
        bool isUnsafe,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory?.CreateClient("RayDispatcher") ?? SharedFallbackHttpClient;
        string endpoint = $"{_options.RayDispatcherUrl.TrimEnd('/')}/dispatch";

        var payload = new RayDispatchRequestDto(
            RequestId: item.Id.ToString("D"),
            StudentId: item.StudentId,
            GroupId: item.GroupId,
            EnrollmentId: item.EnrollmentId?.ToString("D"),
            RequestedEnrollmentId: item.RequestedEnrollmentId?.ToString("D"),
            IsUnsafe: isUnsafe,
            SlowMinMs: _options.SlowCallMinMs,
            SlowMaxMs: _options.SlowCallMaxMs,
            MaxRetries: Math.Max(0, _options.RayTaskMaxRetries),
            FaultsEnabled: _options.FaultsEnabled,
            FaultsDir: string.IsNullOrWhiteSpace(_options.FaultsDirectory) ? "/faults" : _options.FaultsDirectory);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiAuthToken);
        if (!string.IsNullOrWhiteSpace(_options.InternalReceiverSecret))
        {
            requestMessage.Headers.TryAddWithoutValidation("X-Ray-Internal-Secret", _options.InternalReceiverSecret);
        }

        using var response = await client.SendAsync(requestMessage, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ray dispatcher '{endpoint}' повернув HTTP {(int)response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<RayDispatchResponseDto>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Ray dispatcher повернув порожню JSON-відповідь.");

        bool committed = string.Equals(result.Status, "done", StringComparison.Ordinal);

        if (committed && _options.AllowTestOverrides && _receiverService.TestHookAfterCommitAsync != null)
        {
            await _receiverService.TestHookAfterCommitAsync(item);
        }

        return (result.Status, result.RejectionReason, committed);
    }

    private async Task<(string Status, string? Reason, bool Committed)> ExecuteInProcessWithRetryAsync(
        QueuedRequestItem item,
        bool isUnsafe,
        CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(1, _options.RayTaskMaxRetries + 1);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Симуляція повільної перевірки поза критичною секцією та до виклику приймача результатів
            int slowDelayMs = isUnsafe
                ? _options.SlowCallMinMs
                : Random.Shared.Next(_options.SlowCallMinMs, _options.SlowCallMaxMs + 1);
            if (slowDelayMs > 0)
            {
                await Task.Delay(slowDelayMs, cancellationToken);
            }

            var req = new RayEffectApplyRequest(
                RequestId: item.Id,
                StudentId: item.StudentId,
                GroupId: item.GroupId,
                EnrollmentId: item.EnrollmentId,
                RequestedEnrollmentId: item.RequestedEnrollmentId,
                IsUnsafe: isUnsafe,
                WorkerId: $"inproc:{_options.InstanceId}",
                RayAttempt: attempt);

            var res = await _receiverService.ApplyEffectAsync(req, trackedItem: item, cancellationToken);

            try
            {
                // Контрольована відмова О3.4: ефект уже зафіксовано приймачем (HTTP 200),
                // але задача аварійно падає до підтвердження виконання (ack) у диспетчері.
                FaultInjector.MaybeTriggerAfterEffectBeforeAckInProcess(_options, item.Id, item.StudentId);
                bool committed = string.Equals(res.Status, "done", StringComparison.Ordinal);
                return (res.Status, res.RejectionReason, committed);
            }
            catch (SimulatedRayWorkerCrashException ex) when (attempt < maxAttempts)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "ray.task.retry_scheduled", new Dictionary<string, object?>
                {
                    ["request_id"] = item.Id.ToString("D"),
                    ["student_id"] = item.StudentId,
                    ["group_id"] = item.GroupId,
                    ["attempt"] = attempt,
                    ["next_attempt"] = attempt + 1,
                    ["reason"] = ex.Message
                });
            }
        }

        throw new InvalidOperationException(
            $"Задачу '{item.Id}' не вдалося підтвердити після {maxAttempts} спроб у Ray.");
    }

    private sealed record RayDispatchRequestDto(
        [property: JsonPropertyName("request_id")] string RequestId,
        [property: JsonPropertyName("student_id")] string StudentId,
        [property: JsonPropertyName("group_id")] string GroupId,
        [property: JsonPropertyName("enrollment_id")] string? EnrollmentId,
        [property: JsonPropertyName("requested_enrollment_id")] string? RequestedEnrollmentId,
        [property: JsonPropertyName("is_unsafe")] bool IsUnsafe,
        [property: JsonPropertyName("slow_min_ms")] int SlowMinMs,
        [property: JsonPropertyName("slow_max_ms")] int SlowMaxMs,
        [property: JsonPropertyName("max_retries")] int MaxRetries,
        [property: JsonPropertyName("faults_enabled")] bool FaultsEnabled,
        [property: JsonPropertyName("faults_dir")] string FaultsDir);

    private sealed record RayDispatchResponseDto(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("rejection_reason")] string? RejectionReason,
        [property: JsonPropertyName("assigned_group_id")] string? AssignedGroupId,
        [property: JsonPropertyName("roster_recorded")] bool RosterRecorded,
        [property: JsonPropertyName("already_applied")] bool AlreadyApplied,
        [property: JsonPropertyName("delivery_count")] int DeliveryCount);
}
