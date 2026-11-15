using EnrollmentService.Http;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Web ЛР3 (О3.1): Вхід користувача за паролем та видача непрозорого серверного Bearer-токена
        app.MapPost("/v1/auth/login", async (
            HttpContext ctx,
            WebAuthService authService,
            ServiceOptions opts) =>
        {
            var (dto, problemResult) = await HttpPayloadHelper.ReadAndValidateAsync<WebLoginRequestDto>(ctx, opts);
            if (problemResult != null)
            {
                return problemResult;
            }

            var result = await authService.LoginAsync(dto!, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        // Web ЛР3 (О3.1): Вихід користувача та серверний відклик непрозорого токена (дійсний на app-a та app-b)
        app.MapPost("/v1/auth/logout", async (
            HttpContext ctx,
            WebAuthService authService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireAuthenticatedUser(ctx, opts, out var webUser, out var authProblem))
            {
                return authProblem!;
            }

            await authService.LogoutAsync(webUser!, ctx.RequestAborted);
            return Results.Ok(new
            {
                status = "logged_out",
                user_id = webUser!.UserId,
                instance_id = opts.InstanceId
            });
        });

        // Web ЛР3 (О3.1): Поточний профіль автентифікованого користувача
        app.MapGet("/v1/me", async (
            HttpContext ctx,
            WebAuthService authService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireAuthenticatedUser(ctx, opts, out var webUser, out var authProblem))
            {
                return authProblem!;
            }

            var result = await authService.GetMeAsync(webUser!, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        return app;
    }
}
