using EnrollmentService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentService.Http;

public static class ProblemDetailsMapper
{
    public static ProblemDetails BuildProblemDetails(
        HttpContext ctx,
        ServiceOptions opts,
        int statusCode,
        string title,
        string detail,
        string errorCode,
        IDictionary<string, object?>? extraExtensions = null)
    {
        string requestId = ctx.Items["RequestId"] as string ?? Guid.NewGuid().ToString("D");
        var problem = new ProblemDetails
        {
            Type = $"https://urn.c10.edu.ua/problems/{errorCode}",
            Title = title,
            Status = statusCode,
            Detail = detail,
            Instance = ctx.Request.Path.Value ?? "/"
        };

        problem.Extensions["error_code"] = errorCode;
        problem.Extensions["request_id"] = requestId;
        problem.Extensions["instance_id"] = opts.InstanceId;
        problem.Extensions["timestamp_utc"] = DateTimeOffset.UtcNow;

        if (extraExtensions != null)
        {
            foreach (var kv in extraExtensions)
            {
                problem.Extensions[kv.Key] = kv.Value;
            }
        }

        return problem;
    }

    public static IResult CreateProblemResult(
        HttpContext ctx,
        ServiceOptions opts,
        int statusCode,
        string title,
        string detail,
        string errorCode,
        IDictionary<string, object?>? extraExtensions = null)
    {
        var problem = BuildProblemDetails(ctx, opts, statusCode, title, detail, errorCode, extraExtensions);
        return Results.Problem(
            type: problem.Type,
            title: problem.Title,
            detail: problem.Detail,
            instance: problem.Instance,
            statusCode: problem.Status,
            extensions: problem.Extensions);
    }

    public static IResult ToProblemResult(
        this ServiceResult result,
        HttpContext ctx,
        ServiceOptions opts)
    {
        return CreateProblemResult(
            ctx,
            opts,
            result.StatusCode,
            result.ErrorTitle ?? "Помилка операції",
            result.ErrorDetail ?? "Сталася помилка виконання операції.",
            result.ErrorCode ?? "operation_failed",
            result.Extensions);
    }

    public static async Task WriteProblemResponseAsync(
        HttpContext ctx,
        ServiceOptions opts,
        int statusCode,
        string title,
        string detail,
        string errorCode)
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }

        var problem = BuildProblemDetails(ctx, opts, statusCode, title, detail, errorCode);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/problem+json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json; charset=utf-8");
    }
}
