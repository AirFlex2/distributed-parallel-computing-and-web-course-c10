using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        // Опис кореневої точки API
        app.MapGet("/", (ServiceOptions opts) => Results.Ok(new
        {
            service = "C10 Enrollment Service (Web + DPC, Lab 1)",
            instance_id = opts.InstanceId,
            endpoints = new
            {
                health = "/health",
                metrics = "/metrics",
                disciplines = "/v1/disciplines",
                enrollments = "/v1/enrollments",
                enrollment_requests = "/v1/enrollment-requests",
                group_seats = "/v1/group-seats",
                decanate_roster = "/v1/decanate-roster",
                distribute_second_choice = "/v1/groups/{id}/distribute-second-choice"
            },
            standalone_client = "Запустіть окремий клієнт поза Docker Compose: pwsh client/start_client.ps1 (http://localhost:3000)"
        }));

        // ПУНКТ О1.6 (Web) та О1.2 (РПО): Службовий маршрут перевірки живучості GET /health
        app.MapGet("/health", (EnrollmentWorkerPool workerPool, IBoundedRequestQueue queue, ServiceOptions opts) =>
        {
            return Results.Ok(new
            {
                status = workerPool.IsShuttingDown ? "shutting_down" : "ok",
                instance_id = opts.InstanceId,
                timestamp_utc = DateTimeOffset.UtcNow,
                queue_length = queue.Count,
                queue_capacity = queue.Capacity,
                allow_test_overrides = opts.AllowTestOverrides
            });
        });

        // ПУНКТ О1.5 та Д1.2 (РПО): Службовий маршрут GET /metrics
        app.MapGet("/metrics", (
            HttpContext ctx,
            MetricsCollector metrics,
            IBoundedRequestQueue queue,
            ServiceOptions opts,
            EnrollmentWorkerPool pool) =>
        {
            string? format = ctx.Request.Query["format"];
            if (string.Equals(format, "prometheus", StringComparison.OrdinalIgnoreCase) ||
                (ctx.Request.Headers.Accept.ToString().Contains("text/plain", StringComparison.OrdinalIgnoreCase) && format != "json"))
            {
                return Results.Text(metrics.BuildPrometheusText(queue.Count), "text/plain; version=0.0.4; charset=utf-8");
            }

            var snapshot = metrics.BuildMetricsSnapshot(queue.Count, opts, pool.IsShuttingDown);
            return Results.Ok(snapshot);
        });

        return app;
    }
}
