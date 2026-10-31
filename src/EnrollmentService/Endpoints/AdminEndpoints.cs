using EnrollmentService.Http;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class AdminEndpoints
{
    private static readonly SemaphoreSlim ResetSemaphore = new(1, 1);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Стендовий маршрут для автоматизованих дослідів (захищений прапорцем ALLOW_TEST_OVERRIDES=true)
        app.MapPost("/v1/admin/reset", async (
            HttpContext ctx,
            [FromQuery] bool? unsafeMode,
            [FromQuery] bool? metricsOnly,
            [FromQuery] string? executionModel,
            ServiceOptions opts,
            MetricsCollector metrics,
            EnrollmentWorkerPool workerPool,
            IBoundedRequestQueue queue,
            IDbConnectionFactory connectionFactory,
            IGroupRepository groupRepo,
            IEnrollmentRepository enrollmentRepo,
            IEnrollmentRequestRepository requestRepo,
            IDecanateRepository decanateRepo) =>
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

            bool isPeerSync = ctx.Request.Headers.ContainsKey("X-Peer-Sync");

            if (metricsOnly == true)
            {
                metrics.ResetMeasurementWindow(queue.Count);
                if (unsafeMode.HasValue)
                {
                    opts.UnsafeMode = unsafeMode.Value;
                }
                if (!string.IsNullOrWhiteSpace(executionModel))
                {
                    await workerPool.SwitchExecutionModelAsync(executionModel.Trim());
                }

                if (!isPeerSync)
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiAuthToken);
                        client.DefaultRequestHeaders.Add("X-Peer-Sync", "1");
                        string peerHost = string.Equals(opts.InstanceId, "app-a", StringComparison.OrdinalIgnoreCase) ? "app-b" : "app-a";
                        string queryString = ctx.Request.QueryString.Value ?? string.Empty;
                        await client.PostAsync($"http://{peerHost}:8080/v1/admin/reset{queryString}", null);
                    }
                    catch { }
                }

                return Results.Ok(new
                {
                    message = "Вікно вимірювання метрик (закон Літтла) перезапущено після розігріву.",
                    unsafe_mode = opts.UnsafeMode,
                    execution_model = opts.ExecutionModel
                });
            }

            await ResetSemaphore.WaitAsync();
            try
            {
                queue.Clear();

                for (int waitIdx = 0; waitIdx < 60 && metrics.InProcessingCount > 0; waitIdx++)
                {
                    await Task.Delay(25);
                }

                if (!string.IsNullOrWhiteSpace(executionModel))
                {
                    await workerPool.SwitchExecutionModelAsync(executionModel.Trim());
                }

                if (unsafeMode.HasValue)
                {
                    opts.UnsafeMode = unsafeMode.Value;
                }

                if (!isPeerSync)
                {
                    await using var conn = await connectionFactory.OpenConnectionAsync();
                    await decanateRepo.ResetRosterAsync(conn);
                    await requestRepo.ResetRequestsAsync(conn);
                    await enrollmentRepo.ResetEnrollmentsAndStudentsAsync(conn);
                    await groupRepo.ResetGroupsAsync(conn);
                }

                metrics.ResetMetrics(0);

                if (!isPeerSync)
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiAuthToken);
                        client.DefaultRequestHeaders.Add("X-Peer-Sync", "1");
                        string peerHost = string.Equals(opts.InstanceId, "app-a", StringComparison.OrdinalIgnoreCase) ? "app-b" : "app-a";
                        string queryString = ctx.Request.QueryString.Value ?? string.Empty;
                        await client.PostAsync($"http://{peerHost}:8080/v1/admin/reset{queryString}", null);
                    }
                    catch { }
                }

                return Results.Ok(new
                {
                    message = "Стан груп (єдиний ресурс discipline_group / VIEW group_seats), відомості деканату, дисциплін, студентів, черги, заявок та метрик успішно скинуто.",
                    unsafe_mode = opts.UnsafeMode,
                    execution_model = opts.ExecutionModel,
                    instance_id = opts.InstanceId
                });
            }
            finally
            {
                ResetSemaphore.Release();
            }
        });

        return app;
    }
}
