using System.Diagnostics;
using EnrollmentService.Http;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class EnrollmentRequestEndpoints
{
    private static readonly SemaphoreSlim AdmissionSemaphore = new(1, 1);

    public static IEndpointRouteBuilder MapEnrollmentRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/enrollment-requests", async (
            HttpContext httpContext,
            CreateEnrollmentRequestDto dto,
            IBoundedRequestQueue queue,
            EnrollmentWorkerPool workerPool,
            MetricsCollector metrics,
            IEnrollmentRequestRepository requestRepo,
            IDbConnectionFactory connectionFactory,
            ServiceOptions opts) =>
        {
            if (workerPool.IsShuttingDown)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    httpContext, opts,
                    StatusCodes.Status503ServiceUnavailable,
                    "Сервіс завершує роботу",
                    "Отримано сигнал SIGTERM (Д1.4). Нові запити не приймаються, триває завершення поточних задач.",
                    "service_shutting_down");
            }

            if (string.IsNullOrWhiteSpace(dto.StudentId) || string.IsNullOrWhiteSpace(dto.GroupId))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    httpContext, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректні параметри запиту",
                    "Поля student_id та group_id є обов'язковими.",
                    "missing_required_fields");
            }

            string studentId = dto.StudentId.Trim();
            string groupId = dto.GroupId.Trim();
            string? secondChoiceGroupId = string.IsNullOrWhiteSpace(dto.SecondChoiceGroupId)
                ? null
                : dto.SecondChoiceGroupId.Trim();

            Guid requestId = dto.RequestId
                ?? (httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var ik) && Guid.TryParse(ik, out var parsedIk)
                    ? parsedIk
                    : Guid.NewGuid());

            await AdmissionSemaphore.WaitAsync();
            try
            {
                // 1. Перевірка на повторний запит із тим самим ID
                var existing = await requestRepo.GetAcceptedByIdAsync(requestId);
                if (existing != null)
                {
                    if (!string.Equals(existing.StudentId, studentId, StringComparison.Ordinal) ||
                        !string.Equals(existing.GroupId, groupId, StringComparison.Ordinal))
                    {
                        return ProblemDetailsMapper.CreateProblemResult(
                            httpContext, opts,
                            StatusCodes.Status409Conflict,
                            "Конфлікт даних ідемпотентного запиту",
                            $"Заявка з ідентифікатором '{requestId}' вже існує з іншими параметрами student_id або group_id.",
                            "idempotency_conflict");
                    }

                    return Results.Accepted($"/v1/enrollment-requests/{existing.Id}", existing);
                }

                // 2. Перевірка стану завершення сервісу (Д1.4) та місткості обмеженої черги (О1.2)
                if (workerPool.IsShuttingDown)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status503ServiceUnavailable,
                        "Сервіс завершує роботу",
                        "Отримано сигнал SIGTERM (Д1.4). Нові запити не приймаються, триває завершення поточних задач.",
                        "service_shutting_down");
                }

                if (queue.Count >= queue.Capacity)
                {
                    metrics.RecordRejected429();
                    httpContext.Response.Headers["Retry-After"] = "1";
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status429TooManyRequests,
                        "Черга запитів переповнена (Backpressure)",
                        $"Перевищено місткість обмеженої черги QUEUE_CAPACITY ({opts.QueueCapacity}). Спробуйте повторити запит через 1 секунду.",
                        "queue_overflow");
                }

                var createdAtUtc = DateTimeOffset.UtcNow;
                var item = new QueuedRequestItem
                {
                    Id = requestId,
                    StudentId = studentId,
                    GroupId = groupId,
                    SecondChoiceGroupId = secondChoiceGroupId,
                    CreatedAtUtc = createdAtUtc,
                    EnqueueTimestamp = Stopwatch.GetTimestamp()
                };

                // 3. О1.1: Спочатку зберігаємо заявку в PostgreSQL зі статусом 'queued' ДО додавання в чергу
                await using var conn = await connectionFactory.OpenConnectionAsync();
                await requestRepo.InsertQueuedRequestAsync(item, conn);

                // 4. О1.2 / Д1.4: Додаємо заявку в чергу; якщо не вдалося — видаляємо запис із БД і повертаємо 503 або 429
                if (workerPool.IsShuttingDown || !metrics.TryEnqueueAndRecordAccepted(queue, item))
                {
                    await requestRepo.DeleteRequestAsync(item.Id, conn);

                    if (workerPool.IsShuttingDown)
                    {
                        return ProblemDetailsMapper.CreateProblemResult(
                            httpContext, opts,
                            StatusCodes.Status503ServiceUnavailable,
                            "Сервіс завершує роботу",
                            "Отримано сигнал SIGTERM (Д1.4). Нові запити не приймаються, триває завершення поточних задач.",
                            "service_shutting_down");
                    }

                    metrics.RecordRejected429();
                    httpContext.Response.Headers["Retry-After"] = "1";
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status429TooManyRequests,
                        "Черга запитів переповнена (Backpressure)",
                        $"Перевищено місткість обмеженої черги QUEUE_CAPACITY ({opts.QueueCapacity}). Спробуйте повторити запит через 1 секунду.",
                        "queue_overflow");
                }

                var response = new EnrollmentAcceptedResponseDto(
                    item.Id,
                    item.StudentId,
                    item.GroupId,
                    "queued",
                    item.CreatedAtUtc,
                    item.SecondChoiceGroupId);

                return Results.Accepted($"/v1/enrollment-requests/{item.Id}", response);
            }
            finally
            {
                AdmissionSemaphore.Release();
            }
        });

        app.MapGet("/v1/enrollment-requests/{id:guid}", async (
            Guid id,
            HttpContext ctx,
            MetricsCollector metrics,
            IEnrollmentRequestRepository requestRepo,
            ServiceOptions opts) =>
        {
            var status = await requestRepo.MarkFirstReadAndGetStatusAsync(id, DateTimeOffset.UtcNow, ctx.RequestAborted);
            if (status != null)
            {
                metrics.RecordFirstReadIfCompleted(id);
                var timings = metrics.GetItemTimings(id);

                double? qWait = timings.QueueWaitMs ?? (status.StartedAt.HasValue ? Math.Round((status.StartedAt.Value - status.CreatedAt).TotalMilliseconds, 3) : null);
                double? proc = timings.ProcessingMs ?? (status.FinishedAt.HasValue && status.StartedAt.HasValue ? Math.Round((status.FinishedAt.Value - status.StartedAt.Value).TotalMilliseconds, 3) : null);
                double? deliv = timings.DeliveryWaitMs ?? (status.FirstReadAt.HasValue && status.FinishedAt.HasValue ? Math.Round(Math.Max(0, (status.FirstReadAt.Value - status.FinishedAt.Value).TotalMilliseconds), 3) : null);

                return Results.Ok(status with
                {
                    QueueWaitMs = qWait,
                    ProcessingTimeMs = proc,
                    DeliveryWaitMs = deliv
                });
            }

            var activeStatus = await requestRepo.GetStatusByIdAsync(id, ctx.RequestAborted);
            if (activeStatus == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status404NotFound,
                    "Заявку не знайдено",
                    $"Заявка з ідентифікатором '{id}' відсутня в базі даних.",
                    "request_not_found");
            }

            var activeTimings = metrics.GetItemTimings(id);
            double? actQWait = activeTimings.QueueWaitMs ?? (activeStatus.StartedAt.HasValue ? Math.Round((activeStatus.StartedAt.Value - activeStatus.CreatedAt).TotalMilliseconds, 3) : null);
            double? actProc = activeTimings.ProcessingMs ?? (activeStatus.FinishedAt.HasValue && activeStatus.StartedAt.HasValue ? Math.Round((activeStatus.FinishedAt.Value - activeStatus.StartedAt.Value).TotalMilliseconds, 3) : null);
            double? actDeliv = activeTimings.DeliveryWaitMs ?? (activeStatus.FirstReadAt.HasValue && activeStatus.FinishedAt.HasValue ? Math.Round(Math.Max(0, (activeStatus.FirstReadAt.Value - activeStatus.FinishedAt.Value).TotalMilliseconds), 3) : null);

            return Results.Ok(activeStatus with
            {
                QueueWaitMs = actQWait,
                ProcessingTimeMs = actProc,
                DeliveryWaitMs = actDeliv
            });
        });

        return app;
    }
}
