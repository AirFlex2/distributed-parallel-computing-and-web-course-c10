using System.Security.Cryptography;
using System.Text;
using EnrollmentService.Repositories;
using EnrollmentService.Services;

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

        PathString path = context.Request.Path;

        // Публічні маршрути, які не потребують автентифікації (але login і health завжди відкриті)
        if (path.Equals("/", StringComparison.Ordinal) ||
            path.Equals("/health", StringComparison.Ordinal) ||
            path.StartsWithSegments("/v1/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        string? rawToken = ExtractBearerToken(context.Request.Headers.Authorization.ToString(), out bool hasAuthHeader);
        bool isDpcServiceToken = rawToken != null && FixedTimeEqualsToken(rawToken, _options.ApiAuthToken);

        AuthenticatedWebUser? webUser = null;
        if (rawToken != null && !isDpcServiceToken)
        {
            var webAuthService = context.RequestServices?.GetService<WebAuthService>();
            if (webAuthService != null)
            {
                webUser = await webAuthService.ValidateBearerTokenAsync(rawToken, context.RequestAborted);
            }
        }

        if (webUser != null)
        {
            context.Items["WebUser"] = webUser;
        }

        bool isWebOnlyRoute = path.StartsWithSegments("/v1/auth/logout", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/me", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/enrollments", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/groups", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/students", StringComparison.OrdinalIgnoreCase);

        bool isDpcServiceOnlyRoute = path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/compute", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/internal", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/group-seats", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/v1/decanate-roster", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWithSegments("/v1/enrollment-requests", StringComparison.OrdinalIgnoreCase)
                && HttpMethods.IsPost(context.Request.Method));

        bool isSharedReadRoute = path.StartsWithSegments("/v1/enrollment-requests", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsGet(context.Request.Method);

        if (isWebOnlyRoute)
        {
            // Суворе розмежування (Web О3.1): службовий токен РПО (API_AUTH_TOKEN)
            // НЕ може використовуватися на маршрутах користувачів/персоналу Web-застосунку.
            if (webUser == null)
            {
                await WriteUnauthorizedAsync(
                    context,
                    "Необхідна автентифікація користувача Web (Authorization: Bearer <token>).",
                    "unauthorized");
                return;
            }
        }
        else if (isDpcServiceOnlyRoute)
        {
            if (!isDpcServiceToken)
            {
                await WriteUnauthorizedAsync(
                    context,
                    "Відсутній або недійсний службовий Bearer-токен РПО.",
                    "unauthorized");
                return;
            }
        }
        else if (isSharedReadRoute)
        {
            if (!isDpcServiceToken && webUser == null)
            {
                await WriteUnauthorizedAsync(
                    context,
                    "Необхідна автентифікація (токен користувача Web або службовий токен РПО).",
                    "unauthorized");
                return;
            }
        }
        else if (path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {
            // Інші публічні /v1/* маршрути (зокрема GET /v1/disciplines):
            // якщо передано заголовок Authorization, він має бути валідним токеном.
            if (hasAuthHeader && !isDpcServiceToken && webUser == null)
            {
                await WriteUnauthorizedAsync(
                    context,
                    "Передано недійсний токен у заголовку Authorization: Bearer.",
                    "unauthorized");
                return;
            }
        }

        await _next(context);
    }

    private Task WriteUnauthorizedAsync(HttpContext context, string detail, string errorCode)
        => ProblemDetailsMapper.WriteProblemResponseAsync(
            context,
            _options,
            StatusCodes.Status401Unauthorized,
            "Неавторизований доступ",
            detail,
            errorCode);

    private static string? ExtractBearerToken(string headerValue, out bool hasAuthHeader)
    {
        hasAuthHeader = !string.IsNullOrWhiteSpace(headerValue);
        if (!hasAuthHeader)
        {
            return null;
        }

        string trimmed = headerValue.Trim();
        const string prefix = "Bearer ";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string token = trimmed.Substring(prefix.Length).Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static bool FixedTimeEqualsToken(string provided, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }
}
