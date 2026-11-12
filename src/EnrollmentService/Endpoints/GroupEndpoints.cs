using EnrollmentService.Http;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class GroupEndpoints
{
    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        // Закриття запису на групу працівником деканату (Web С10)
        app.MapPost("/v1/groups/{id}/close", async (
            string id,
            HttpContext ctx,
            GroupRepository groupRepo,
            ServiceOptions opts) =>
        {
            var group = await groupRepo.CloseGroupAsync(id, ctx.RequestAborted);
            if (group == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status404NotFound,
                    "Групу дисципліни не знайдено",
                    $"Група з ідентифікатором '{id}' не існує.",
                    "group_not_found");
            }

            var groupDto = new WebDisciplineGroupDto(
                group.Id,
                group.DisciplineId,
                group.GroupCode,
                group.Seats,
                group.Available,
                group.OpensAt,
                group.ClosesAt,
                EnrollmentDomainRules.FormatInUniversityTimeZone(group.OpensAt, group.UniversityTz),
                EnrollmentDomainRules.FormatInUniversityTimeZone(group.ClosesAt, group.UniversityTz),
                group.UniversityTz,
                group.Closed,
                false);

            return Results.Ok(groupDto);
        });

        // Читає стан групи через SQL-представлення VIEW group_seats поверх discipline_group та decanate_roster (§1.10)
        app.MapGet("/v1/group-seats/{id}", async (
            string id,
            HttpContext ctx,
            GroupRepository groupRepo,
            ServiceOptions opts) =>
        {
            var seats = await groupRepo.GetGroupSeatsByIdAsync(id, ctx.RequestAborted);
            if (seats == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status404NotFound,
                    "Навчальну групу не знайдено",
                    $"Група з ідентифікатором '{id}' не існує.",
                    "group_not_found");
            }

            return Results.Ok(seats);
        });

        app.MapGet("/v1/group-seats", async (
            HttpContext ctx,
            GroupRepository groupRepo) =>
        {
            var list = await groupRepo.GetAllGroupSeatsAsync(ctx.RequestAborted);
            return Results.Ok(list);
        });

        // Перегляд відомості деканату за request_id, group_id або student_id (С10 РПО п. 2, 6)
        app.MapGet("/v1/decanate-roster", async (
            HttpContext ctx,
            DecanateRepository decanateRepo,
            ServiceOptions opts) =>
        {
            string? groupId = ctx.Request.Query["group_id"].FirstOrDefault();
            string? studentId = ctx.Request.Query["student_id"].FirstOrDefault();
            string? requestIdRaw = ctx.Request.Query["request_id"].FirstOrDefault();

            Guid? requestIdFilter = null;
            if (!string.IsNullOrWhiteSpace(requestIdRaw))
            {
                if (!Guid.TryParse(requestIdRaw, out Guid parsedReqId))
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        ctx, opts,
                        StatusCodes.Status400BadRequest,
                        "Некоректний параметр request_id",
                        "Параметр request_id має бути коректним UUID.",
                        "invalid_request_id");
                }
                requestIdFilter = parsedReqId;
            }

            var items = await decanateRepo.GetRosterEntriesAsync(groupId, studentId, requestIdFilter, ctx.RequestAborted);
            return Results.Ok(new { items, count = items.Count });
        });

        return app;
    }
}
