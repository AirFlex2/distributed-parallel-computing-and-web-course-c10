using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
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
                decanate_roster = "/v1/decanate-roster"
            },
            client = "http://localhost:3000"
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

        // ПУНКТ О1.5 та Д1.2 (РПО): Службовий маршрут GET /metrics (JSON)
        app.MapGet("/metrics", (
            MetricsCollector metrics,
            IBoundedRequestQueue queue,
            ServiceOptions opts,
            EnrollmentWorkerPool pool) =>
        {
            var snapshot = metrics.BuildMetricsSnapshot(queue.Count, opts, pool.IsShuttingDown);
            return Results.Ok(snapshot);
        });

        return app;
    }
}
