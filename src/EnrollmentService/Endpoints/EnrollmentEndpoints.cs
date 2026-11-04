using System.Globalization;
using EnrollmentService.Http;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class EnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Довідковий маршрут: список студентів у БД (для вибору X-User-Id у клієнті О1.4, О1.7)
        app.MapGet("/v1/students", async (EnrollmentRepository enrollmentRepo, HttpContext ctx) =>
        {
            var students = await enrollmentRepo.GetStudentsAsync(ctx.RequestAborted);
            var dtos = students.Select(s => new WebStudentDto(
                s.Id,
                s.Email,
                s.FullName,
                s.MaxDisciplines,
                s.ActiveCount,
                s.ActiveEcts,
                EnrollmentDomainRules.MaxSemesterEcts
            )).ToList();
            return Results.Ok(new { items = dtos });
        });

        // Маршрут С10 (Web): GET /v1/disciplines — список дисциплін із групами, вікнами запису та залишком місць
        app.MapGet("/v1/disciplines", async (
            HttpContext ctx,
            EnrollmentService.Services.EnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            int limit = 20;
            if (ctx.Request.Query.TryGetValue("limit", out var limitVals) && !string.IsNullOrWhiteSpace(limitVals))
            {
                if (!int.TryParse(limitVals.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > 50)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        ctx, opts,
                        StatusCodes.Status400BadRequest,
                        "Некоректний параметр пагінації limit",
                        "Параметр limit має бути цілим числом у діапазоні від 1 до 50.",
                        "invalid_limit");
                }
            }

            string? cursorRaw = ctx.Request.Query["after"].FirstOrDefault()
                ?? ctx.Request.Query["cursor"].FirstOrDefault();

            bool includeTest = opts.AllowTestOverrides
                && bool.TryParse(ctx.Request.Query["include_test"].FirstOrDefault(), out var incTest)
                && incTest;

            var result = await enrollmentService.GetDisciplinesAsync(limit, cursorRaw, includeTest, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        // Головна операція POST /v1/enrollments (Web О1.4, О2.2, Д1.1, Д1.5)
        app.MapPost("/v1/enrollments", async (
            HttpContext ctx,
            EnrollmentService.Services.EnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireCallerStudentId(ctx, opts, out string studentId, out var userHeaderProblem))
            {
                return userHeaderProblem!;
            }

            if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var ikHeader) || string.IsNullOrWhiteSpace(ikHeader))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Відсутній або порожній заголовок Idempotency-Key",
                    "Заголовок 'Idempotency-Key' є обов'язковим для створення запису (POST /v1/enrollments) і не може бути порожнім.",
                    "missing_idempotency_key");
            }

            string idempotencyKey = ikHeader.ToString().Trim();
            if (idempotencyKey.Length > 128)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректний заголовок Idempotency-Key",
                    "Довжина ключа Idempotency-Key не може перевищувати 128 символів.",
                    "invalid_idempotency_key");
            }

            var (dto, problemResult) = await HttpPayloadHelper.ReadAndValidateAsync<CreateWebEnrollmentDto>(ctx, opts);
            if (problemResult != null)
            {
                return problemResult;
            }

            var result = await enrollmentService.CreateEnrollmentAsync(
                studentId,
                dto!,
                idempotencyKey: idempotencyKey,
                cancellationToken: ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            string locationUrl = $"/v1/enrollments/{result.Value!.Id}";
            ctx.Response.Headers.Location = locationUrl;
            return Results.Created(locationUrl, result.Value);
        });

        // Список записів студента з курсорним пагінуванням (Web О1.5)
        app.MapGet("/v1/enrollments", async (
            HttpContext ctx,
            EnrollmentService.Services.EnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireCallerStudentId(ctx, opts, out string studentId, out var userHeaderProblem))
            {
                return userHeaderProblem!;
            }

            if (ctx.Request.Query.ContainsKey("student_id"))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Параметр student_id у рядку запиту заборонено",
                    "Список записів повертається виключно для користувача із заголовка X-User-Id.",
                    "query_student_override_forbidden");
            }

            int limit = 10;
            if (ctx.Request.Query.TryGetValue("limit", out var limitVals) && !string.IsNullOrWhiteSpace(limitVals))
            {
                if (!int.TryParse(limitVals.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > 50)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        ctx, opts,
                        StatusCodes.Status400BadRequest,
                        "Некоректний параметр limit",
                        "Параметр limit має бути цілим числом від 1 до 50 включно.",
                        "invalid_limit");
                }
            }

            string? cursorRaw = ctx.Request.Query["cursor"].FirstOrDefault()
                ?? ctx.Request.Query["after"].FirstOrDefault();

            var result = await enrollmentService.GetStudentEnrollmentsAsync(studentId, limit, cursorRaw, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        // Отримання конкретного запису студента (Web С10)
        app.MapGet("/v1/enrollments/{id}", async (
            string id,
            HttpContext ctx,
            EnrollmentService.Services.EnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireCallerStudentId(ctx, opts, out string callerStudentId, out var userHeaderProblem))
            {
                return userHeaderProblem!;
            }

            if (!Guid.TryParse(id, out var enrollmentGuid))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректний ідентифікатор запису",
                    "Параметр id має бути валідним UUID.",
                    "invalid_enrollment_id");
            }

            var result = await enrollmentService.GetEnrollmentByIdAsync(callerStudentId, enrollmentGuid, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        // Підтвердження запису студентом (Web О1.1: held -> confirmed)
        app.MapPost("/v1/enrollments/{id}/confirm", async (
            string id,
            HttpContext ctx,
            EnrollmentService.Services.EnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireCallerStudentId(ctx, opts, out string callerStudentId, out var userHeaderProblem))
            {
                return userHeaderProblem!;
            }

            if (!Guid.TryParse(id, out var enrollmentGuid))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректний ідентифікатор запису",
                    "Параметр id має бути валідним UUID.",
                    "invalid_enrollment_id");
            }

            var result = await enrollmentService.ConfirmEnrollmentAsync(callerStudentId, enrollmentGuid, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        // Скасування запису з поверненням місця в групі (Web С10)
        app.MapPost("/v1/enrollments/{id}/cancel", async (
            string id,
            HttpContext ctx,
            EnrollmentService.Services.EnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireCallerStudentId(ctx, opts, out string callerStudentId, out var userHeaderProblem))
            {
                return userHeaderProblem!;
            }

            if (!Guid.TryParse(id, out var enrollmentGuid))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректний ідентифікатор запису",
                    "Параметр id має бути валідним UUID.",
                    "invalid_enrollment_id");
            }

            var result = await enrollmentService.CancelEnrollmentAsync(callerStudentId, enrollmentGuid, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        return app;
    }
}
