using System.Globalization;
using System.Text.Json;
using EnrollmentService.Http;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class EnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Довідковий маршрут: список наперед створених студентів у БД (для вибору X-User-Id у клієнті О1.4, О1.7)
        app.MapGet("/v1/students", async (IEnrollmentService enrollmentService, HttpContext ctx) =>
        {
            var students = await enrollmentService.GetStudentsAsync(ctx.RequestAborted);
            return Results.Ok(new { items = students });
        });

        // Маршрут С10 (Web): GET /v1/disciplines — список дисциплін із групами, вікнами запису та актуальним залишком місць
        app.MapGet("/v1/disciplines", async (
            HttpContext ctx,
            IEnrollmentService enrollmentService,
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

            string? afterDisciplineId = ctx.Request.Query["after"].FirstOrDefault()
                ?? ctx.Request.Query["cursor"].FirstOrDefault();

            bool includeTest = opts.AllowTestOverrides
                && bool.TryParse(ctx.Request.Query["include_test"].FirstOrDefault(), out var incTest)
                && incTest;

            var result = await enrollmentService.GetDisciplinesAsync(limit, afterDisciplineId, includeTest, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        // Головна операція POST /v1/enrollments (Web О1.4, Д1.1, Д1.3, Д1.5)
        app.MapPost("/v1/enrollments", async (
            HttpContext ctx,
            IEnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireCallerStudentId(ctx, opts, out string studentId, out var userHeaderProblem))
            {
                return userHeaderProblem!;
            }

            CreateWebEnrollmentDto? dto;
            try
            {
                dto = await JsonSerializer.DeserializeAsync<CreateWebEnrollmentDto>(
                    ctx.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Синтаксично некоректний JSON",
                    "Тіло запиту не є коректним JSON-документом.",
                    "malformed_json");
            }

            if (dto == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Порожнє тіло запиту",
                    "Очікується JSON-об'єкт із полем group_id.",
                    "empty_json_body");
            }

            // Заборона визначення власника з тіла запиту (типова помилка ЛР1 Web)
            if (dto.StudentId != null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status422UnprocessableEntity,
                    "Визначення власника з тіла запиту заборонено",
                    "Поле 'student_id' не дозволено передавати у тілі POST /v1/enrollments. Власник запису визначається виключно із заголовка X-User-Id.",
                    "owner_in_body_forbidden");
            }

            string? simulatedNowHeader = ctx.Request.Headers["X-Simulated-Now"].FirstOrDefault();
            var result = await enrollmentService.CreateEnrollmentAsync(studentId, dto, simulatedNowHeader, ctx.RequestAborted);

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
            IEnrollmentService enrollmentService,
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

            string? cursorRaw = ctx.Request.Query["after"].FirstOrDefault()
                ?? ctx.Request.Query["cursor"].FirstOrDefault();

            var result = await enrollmentService.GetEnrollmentsAsync(studentId, limit, cursorRaw, ctx.RequestAborted);
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
            IEnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireCallerStudentId(ctx, opts, out string callerStudentId, out var userHeaderProblem))
            {
                return userHeaderProblem!;
            }

            var result = await enrollmentService.GetEnrollmentByIdAsync(callerStudentId, id, ctx.RequestAborted);
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
            IEnrollmentService enrollmentService,
            ServiceOptions opts) =>
        {
            if (!RequestIdentity.TryRequireCallerStudentId(ctx, opts, out string callerStudentId, out var userHeaderProblem))
            {
                return userHeaderProblem!;
            }

            var result = await enrollmentService.CancelEnrollmentAsync(callerStudentId, id, ctx.RequestAborted);
            if (!result.IsSuccess)
            {
                return result.ToProblemResult(ctx, opts);
            }

            return Results.Ok(result.Value);
        });

        return app;
    }
}
