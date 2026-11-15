using System.Security.Cryptography;
using System.Text;
using EnrollmentService.Http;
using EnrollmentService.Services;
using EnrollmentService.Services.Processing;

namespace EnrollmentService.Endpoints;

/// <summary>
/// Внутрішній endpoint приймача результатів Ray (РПО ЛР3: О3.1, О3.3, О3.4).
/// Доступний лише з валідним службовим Bearer-токеном РПО (перевіряється у BearerAuthMiddleware)
/// та внутрішнім секретом X-Ray-Internal-Secret.
/// </summary>
public static class InternalReceiverEndpoints
{
    public static void MapInternalReceiverEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/internal/ray-effect-receiver", async (
            HttpContext ctx,
            ServiceOptions opts,
            RayEffectReceiverService receiver) =>
        {
            if (!string.IsNullOrEmpty(opts.InternalReceiverSecret))
            {
                string? providedSecret = ctx.Request.Headers["X-Ray-Internal-Secret"].FirstOrDefault();
                if (string.IsNullOrEmpty(providedSecret) ||
                    !FixedTimeEqualsUtf8(providedSecret, opts.InternalReceiverSecret))
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        ctx, opts, StatusCodes.Status403Forbidden,
                        "Forbidden",
                        "Недійсний внутрішній секрет приймача результатів Ray.",
                        "invalid_internal_receiver_secret");
                }
            }

            var (req, payloadError) = await HttpPayloadHelper.ReadAndValidateAsync<RayEffectApplyRequest>(ctx, opts);
            if (payloadError != null)
            {
                return payloadError;
            }

            if (req == null || req.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(req.StudentId) || string.IsNullOrWhiteSpace(req.GroupId))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts, StatusCodes.Status400BadRequest,
                    "Bad Request",
                    "Некоректний запит до приймача результатів Ray: обов'язкові поля request_id, student_id, group_id.",
                    "invalid_ray_receiver_payload");
            }

            var result = await receiver.ApplyEffectAsync(req, trackedItem: null, ctx.RequestAborted);

            StructuredJsonLogger.LogEvent(opts.InstanceId, "ray.receiver.delivery", new Dictionary<string, object?>
            {
                ["request_id"] = result.RequestId.ToString("D"),
                ["student_id"] = req.StudentId,
                ["group_id"] = req.GroupId,
                ["status"] = result.Status,
                ["already_applied"] = result.AlreadyApplied,
                ["delivery_count"] = result.DeliveryCount,
                ["worker_id"] = req.WorkerId,
                ["ray_attempt"] = req.RayAttempt
            });

            return Results.Ok(result);
        });
    }

    private static bool FixedTimeEqualsUtf8(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
