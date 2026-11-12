namespace EnrollmentService.Http;

public sealed class BearerAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServiceOptions _options;

    public BearerAuthMiddleware(RequestDelegate next, ServiceOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        bool requiresBearerAuth = context.Request.Path.StartsWithSegments("/metrics")
            || context.Request.Path.StartsWithSegments("/v1/enrollment-requests")
            || context.Request.Path.StartsWithSegments("/v1/group-seats")
            || context.Request.Path.StartsWithSegments("/v1/decanate-roster")
            || context.Request.Path.StartsWithSegments("/v1/admin")
            || context.Request.Path.StartsWithSegments("/v1/groups")
            || context.Request.Path.StartsWithSegments("/v1/compute");

        string authHeader = context.Request.Headers.Authorization.ToString();
        string expected = $"Bearer {_options.ApiAuthToken}";

        if (requiresBearerAuth)
        {
            if (string.IsNullOrWhiteSpace(authHeader) || !string.Equals(authHeader.Trim(), expected, StringComparison.Ordinal))
            {
                await ProblemDetailsMapper.WriteProblemResponseAsync(
                    context, _options,
                    StatusCodes.Status401Unauthorized,
                    "Неавторизований доступ",
                    "Відсутній або недійсний токен у заголовку Authorization: Bearer.",
                    "unauthorized");
                return;
            }
        }
        else if (context.Request.Path.StartsWithSegments("/v1"))
        {
            if (!string.IsNullOrWhiteSpace(authHeader) && !string.Equals(authHeader.Trim(), expected, StringComparison.Ordinal))
            {
                await ProblemDetailsMapper.WriteProblemResponseAsync(
                    context, _options,
                    StatusCodes.Status401Unauthorized,
                    "Неавторизований доступ",
                    "Передано недійсний токен у заголовку Authorization: Bearer.",
                    "unauthorized");
                return;
            }
        }

        await _next(context);
    }
}
