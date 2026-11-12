using System.Diagnostics;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace EnrollmentService.Http;

public sealed class RequestIdentityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServiceOptions _options;

    public RequestIdentityMiddleware(RequestDelegate next, ServiceOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        long startTs = Stopwatch.GetTimestamp();

        string clientRequestId = context.Request.Headers["X-Request-ID"].ToString().Trim();
        string requestId = (!string.IsNullOrWhiteSpace(clientRequestId) && clientRequestId.Length <= 128)
            ? clientRequestId
            : Guid.NewGuid().ToString("D");

        context.Items["RequestId"] = requestId;
        context.Response.Headers["X-Request-ID"] = requestId;
        context.Response.Headers["X-Instance-Id"] = _options.InstanceId;

        string? traceId = null;
        if (EnrollmentDomainRules.TryExtractTraceId(context.Request.Headers["traceparent"].ToString(), out string? parsedTraceId))
        {
            traceId = parsedTraceId;
            context.Items["TraceId"] = traceId;
            context.Response.Headers["X-Trace-Id"] = traceId!;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Request-ID"] = requestId;
            context.Response.Headers["X-Instance-Id"] = _options.InstanceId;
            if (traceId != null)
            {
                context.Response.Headers["X-Trace-Id"] = traceId;
            }
            return Task.CompletedTask;
        });

        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 499;
            }
        }
        catch (PostgresException pgEx)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "database_exception", new Dictionary<string, object?>
            {
                ["request_id"] = requestId,
                ["sql_state"] = pgEx.SqlState,
                ["path"] = context.Request.Path.Value ?? "/"
            });

            if (pgEx.SqlState is "40P01" or "40001")
            {
                await ProblemDetailsMapper.WriteProblemResponseAsync(
                    context, _options,
                    StatusCodes.Status409Conflict,
                    "Конкурентний конфлікт транзакцій бази даних",
                    "Виявлено взаємне блокування або конфлікт серіалізації транзакцій. Безпечно повторіть запит.",
                    "concurrency_conflict");
            }
            else if (pgEx.SqlState == "23505")
            {
                await ProblemDetailsMapper.WriteProblemResponseAsync(
                    context, _options,
                    StatusCodes.Status409Conflict,
                    "Конфлікт унікальності ресурсу",
                    "Запис із такими ключовими атрибутами вже існує.",
                    "duplicate_resource");
            }
            else if (pgEx.SqlState == "23514")
            {
                await ProblemDetailsMapper.WriteProblemResponseAsync(
                    context, _options,
                    StatusCodes.Status409Conflict,
                    "Порушення інваріанта цілісності даних",
                    "Операція порушує обмеження цілісності предметної області у PostgreSQL.",
                    "domain_invariant_violation");
            }
            else
            {
                await ProblemDetailsMapper.WriteProblemResponseAsync(
                    context, _options,
                    StatusCodes.Status500InternalServerError,
                    "Помилка підсистеми бази даних",
                    "Виникла внутрішня помилка під час виконання запиту до бази даних.",
                    "database_error");
            }
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "unhandled_exception", new Dictionary<string, object?>
            {
                ["request_id"] = requestId,
                ["error_type"] = ex.GetType().Name,
                ["detail"] = ex.Message,
                ["path"] = context.Request.Path.Value ?? "/"
            });

            await ProblemDetailsMapper.WriteProblemResponseAsync(
                context, _options,
                StatusCodes.Status500InternalServerError,
                "Внутрішня помилка сервера",
                "Сталася непередбачена помилка обробки запиту.",
                "internal_server_error");
        }
        finally
        {
            double durationMs = Math.Round(Stopwatch.GetElapsedTime(startTs).TotalMilliseconds, 3);
            string routeTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? context.Request.Path.Value
                ?? "/";
            string? userId = context.Request.Headers["X-User-Id"].FirstOrDefault();

            string timestampIso = DateTimeOffset.UtcNow.ToString("O");
            var logEntry = new Dictionary<string, object?>
            {
                ["ts"] = timestampIso,
                ["time"] = timestampIso,
                ["event"] = "http_request",
                ["instance"] = _options.InstanceId,
                ["method"] = context.Request.Method,
                ["route"] = routeTemplate,
                ["path"] = context.Request.Path.Value ?? "/",
                ["status"] = context.Response.StatusCode,
                ["duration_ms"] = durationMs,
                ["duration"] = durationMs,
                ["request_id"] = requestId
            };

            if (traceId != null)
            {
                logEntry["trace_id"] = traceId;
            }

            if (!string.IsNullOrWhiteSpace(userId))
            {
                logEntry["user_id"] = userId;
            }

            StructuredJsonLogger.LogHttpRequest(logEntry);
        }
    }
}
