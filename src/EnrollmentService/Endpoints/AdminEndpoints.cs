using EnrollmentService.Http;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Стендовий маршрут для автоматизованих дослідів (захищений прапорцем ALLOW_TEST_OVERRIDES=true)
        app.MapPost("/v1/admin/reset", async (
            HttpContext ctx,
            [FromQuery] bool? unsafeMode,
            [FromQuery] bool? metricsOnly,
            ServiceOptions opts,
            MetricsCollector metrics,
            IBoundedRequestQueue queue,
            IDbConnectionFactory connectionFactory,
            GroupRepository groupRepo,
            EnrollmentRepository enrollmentRepo,
            EnrollmentRequestRepository requestRepo,
            DecanateRepository decanateRepo) =>
        {
            if (!opts.AllowTestOverrides)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status403Forbidden,
                    "Стендові перевизначення вимкнено",
                    "Маршрут /v1/admin/reset доступний лише при запуску в тестовому режимі (ALLOW_TEST_OVERRIDES=true).",
                    "test_override_disabled");
            }

            if (unsafeMode.HasValue)
            {
                opts.UnsafeMode = unsafeMode.Value;
            }

            if (metricsOnly == true)
            {
                metrics.ResetMeasurementWindow(queue.Count);
                return Results.Ok(new
                {
                    message = "Вікно вимірювання метрик (закон Літтла) перезапущено.",
                    unsafe_mode = opts.UnsafeMode
                });
            }

            queue.Clear();

            for (int waitIdx = 0; waitIdx < 40 && metrics.InProcessingCount > 0; waitIdx++)
            {
                await Task.Delay(25);
            }

            await using (var conn = await connectionFactory.OpenConnectionAsync())
            {
                await decanateRepo.ResetRosterAsync(conn);
                await requestRepo.ResetRequestsAsync(conn);
                await enrollmentRepo.ResetEnrollmentsAndStudentsAsync(conn);
                await groupRepo.ResetGroupsAsync(conn);
            }

            metrics.ResetMetrics(0);

            return Results.Ok(new
            {
                message = "Стан бази даних, черги та метрик успішно скинуто.",
                unsafe_mode = opts.UnsafeMode,
                instance_id = opts.InstanceId
            });
        });

        return app;
    }
}
