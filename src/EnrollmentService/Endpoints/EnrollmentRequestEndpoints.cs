using System.Diagnostics;
using EnrollmentService;
using EnrollmentService.Http;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class EnrollmentRequestEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentRequestEndpoints(this IEndpointRouteBuilder app)
    {
        // ПУНКТ О1.1 (РПО): Асинхронний прийом заявок на запис у дисципліну з відповіддю HTTP 202 Accepted
        app.MapPost("/v1/enrollment-requests", async (
            HttpContext httpContext,
            IBoundedRequestQueue queue,
            EnrollmentWorkerPool workerPool,
            MetricsCollector metrics,
            EnrollmentRequestRepository requestRepo,
            GroupRepository groupRepo,
            EnrollmentRepository enrollmentRepo,
            IDbConnectionFactory connectionFactory,
            ServiceOptions opts) =>
        {
            long arrivalTimestamp = Stopwatch.GetTimestamp();

            if (workerPool.IsShuttingDown)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    httpContext, opts,
                    StatusCodes.Status503ServiceUnavailable,
                    "Сервіс завершує роботу",
                    "Отримано сигнал SIGTERM (Д1.4). Нові запити не приймаються, триває завершення поточних задач.",
                    "service_shutting_down");
            }

            var (dto, problemResult) = await HttpPayloadHelper.ReadAndValidateAsync<CreateEnrollmentRequestDto>(httpContext, opts);
            if (problemResult != null)
            {
                return problemResult;
            }

            string? idempotencyKey = null;
            if (httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var ikHeader) && !string.IsNullOrWhiteSpace(ikHeader))
            {
                idempotencyKey = ikHeader.ToString().Trim();
                if (idempotencyKey.Length > 128)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status400BadRequest,
                        "Некоректний заголовок Idempotency-Key",
                        "Довжина заголовка Idempotency-Key перевищує допустимий ліміт 128 символів.",
                        "invalid_idempotency_key");
                }
            }
            else if (dto!.RequestId.HasValue)
            {
                idempotencyKey = dto.RequestId.Value.ToString("D");
            }

            string studentId = dto!.StudentId!.Trim();
            string groupId = dto!.GroupId!.Trim();
            string payloadFingerprint = $"{studentId}:{groupId}";

            // Швидка перевірка переповнення черги (О1.2)
            if (!queue.TryReserveSlot())
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

                // Якщо черга заповнена, але клієнт повторює раніше прийнятий запит з Idempotency-Key
                // (наприклад, через втрату відповіді в мережі), перевіряємо наявність заявки в БД.
                // Якщо заявка вже збережена — повертаємо 202 Accepted (або 409 при конфлікті), не відхиляючи клієнта з 429.
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    await using var connCheck = await connectionFactory.OpenConnectionAsync(httpContext.RequestAborted);
                    var existing = await requestRepo.GetByIdempotencyKeyAsync(idempotencyKey, connCheck, httpContext.RequestAborted);
                    if (existing != null)
                    {
                        if (!string.Equals(existing.RequestPayloadHash, payloadFingerprint, StringComparison.Ordinal) ||
                            !string.Equals(existing.StudentId, studentId, StringComparison.Ordinal) ||
                            !string.Equals(existing.GroupId, groupId, StringComparison.Ordinal))
                        {
                            return ProblemDetailsMapper.CreateProblemResult(
                                httpContext, opts,
                                StatusCodes.Status409Conflict,
                                "Конфлікт даних ідемпотентного запиту",
                                $"Ідемпотентний ключ '{idempotencyKey}' вже було використано з іншими параметрами student_id або group_id.",
                                "idempotency_conflict");
                        }

                        return Results.Accepted($"/v1/enrollment-requests/{existing.Id}", new EnrollmentAcceptedResponseDto(
                            existing.Id,
                            existing.StudentId,
                            existing.GroupId,
                            existing.Status,
                            existing.CreatedAt));
                    }
                }

                // Сюди потрапляють виключно дійсно нові запити при переповненій черзі (Backpressure):
                metrics.RecordRejected429();
                httpContext.Response.Headers["Retry-After"] = "1";
                return ProblemDetailsMapper.CreateProblemResult(
                    httpContext, opts,
                    StatusCodes.Status429TooManyRequests,
                    "Черга запитів переповнена (Backpressure)",
                    $"Перевищено місткість обмеженої черги QUEUE_CAPACITY ({opts.QueueCapacity}). Спробуйте повторити запит через 1 секунду.",
                    "queue_overflow");
            }

            bool slotConsumedByQueue = false;
            QueuedRequestItem? item = null;

            try
            {
                // Перевірка ідемпотентності (якщо слот успішно зарезервовано).
                // У разі виявлення існуючої заявки зарезервований слот автоматично звільняється в блоці finally.
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    await using var connCheck = await connectionFactory.OpenConnectionAsync(httpContext.RequestAborted);
                    var existing = await requestRepo.GetByIdempotencyKeyAsync(idempotencyKey, connCheck, httpContext.RequestAborted);
                    if (existing != null)
                    {
                        if (!string.Equals(existing.RequestPayloadHash, payloadFingerprint, StringComparison.Ordinal) ||
                            !string.Equals(existing.StudentId, studentId, StringComparison.Ordinal) ||
                            !string.Equals(existing.GroupId, groupId, StringComparison.Ordinal))
                        {
                            return ProblemDetailsMapper.CreateProblemResult(
                                httpContext, opts,
                                StatusCodes.Status409Conflict,
                                "Конфлікт даних ідемпотентного запиту",
                                $"Ідемпотентний ключ '{idempotencyKey}' вже було використано з іншими параметрами student_id або group_id.",
                                "idempotency_conflict");
                        }

                        return Results.Accepted($"/v1/enrollment-requests/{existing.Id}", new EnrollmentAcceptedResponseDto(
                            existing.Id,
                            existing.StudentId,
                            existing.GroupId,
                            existing.Status,
                            existing.CreatedAt));
                    }
                }
                var group = await groupRepo.GetGroupByIdAsync(groupId, httpContext.RequestAborted);
                if (group == null)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status404NotFound,
                        "Групу не знайдено",
                        $"Навчальну групу '{groupId}' не знайдено в базі даних.",
                        "group_not_found");
                }

                if (!group.DpcSandboxEnabled)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status422UnprocessableEntity,
                        "Група не підтримує автономний прийом РПО",
                        $"Навчальна група '{groupId}' є інтерактивною групою Web-контуру (dpc_sandbox_enabled = false). Прямий асинхронний прийом через POST /v1/enrollment-requests дозволено лише для груп автономного стенду РПО (DISC-DPC-01, DISC-TEST-SAFE).",
                        "dpc_sandbox_disabled");
                }

                var student = await enrollmentRepo.GetStudentByIdAsync(studentId, httpContext.RequestAborted);
                if (student == null)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status404NotFound,
                        "Студента не знайдено",
                        $"Студента '{studentId}' не знайдено в системі деканату.",
                        "student_not_found");
                }

                Guid requestId = Guid.NewGuid();
                // Якщо dto.RequestId передано без заголовка Idempotency-Key, зберігаємо його як ID заявки
                if (dto.RequestId.HasValue && !httpContext.Request.Headers.ContainsKey("Idempotency-Key"))
                {
                    requestId = dto.RequestId.Value;
                }

                var createdAtUtc = DateTimeOffset.UtcNow;
                item = new QueuedRequestItem
                {
                    Id = requestId,
                    StudentId = studentId,
                    GroupId = groupId,
                    CreatedAtUtc = createdAtUtc,
                    IdempotencyKey = idempotencyKey,
                    RequestPayloadHash = payloadFingerprint,
                    EnqueueTimestamp = arrivalTimestamp
                };

                await using var conn = await connectionFactory.OpenConnectionAsync(httpContext.RequestAborted);

                bool inserted = await requestRepo.TryInsertQueuedRequestAsync(item, conn, httpContext.RequestAborted);
                if (!inserted)
                {
                    ExistingRequestRecord? existing = null;
                    if (!string.IsNullOrWhiteSpace(idempotencyKey))
                    {
                        existing = await requestRepo.GetByIdempotencyKeyAsync(idempotencyKey, conn, httpContext.RequestAborted);
                    }
                    if (existing == null)
                    {
                        existing = await requestRepo.GetRecordByIdAsync(requestId, conn, httpContext.RequestAborted);
                    }

                    if (existing != null)
                    {
                        if (!string.Equals(existing.RequestPayloadHash, payloadFingerprint, StringComparison.Ordinal) ||
                            !string.Equals(existing.StudentId, studentId, StringComparison.Ordinal) ||
                            !string.Equals(existing.GroupId, groupId, StringComparison.Ordinal))
                        {
                            return ProblemDetailsMapper.CreateProblemResult(
                                httpContext, opts,
                                StatusCodes.Status409Conflict,
                                "Конфлікт даних ідемпотентного запиту",
                                $"Заявка з цим ідентифікатором або ідемпотентним ключем уже існує з іншими параметрами.",
                                "idempotency_conflict");
                        }

                        return Results.Accepted($"/v1/enrollment-requests/{existing.Id}", new EnrollmentAcceptedResponseDto(
                            existing.Id,
                            existing.StudentId,
                            existing.GroupId,
                            existing.Status,
                            existing.CreatedAt));
                    }

                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status500InternalServerError,
                        "Помилка створення заявки",
                        "Не вдалося зафіксувати унікальну заявку в базі даних.",
                        "request_insert_failed");
                }

                if (workerPool.IsShuttingDown)
                {
                    await requestRepo.DeleteRequestAsync(item.Id, conn, httpContext.RequestAborted);
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status503ServiceUnavailable,
                        "Сервіс завершує роботу",
                        "Отримано сигнал SIGTERM (Д1.4). Нові запити не приймаються, триває завершення поточних задач.",
                        "service_shutting_down");
                }

                workerPool.TrackEnqueued(item.Id);
                bool enqueued = metrics.TryEnqueueReservedAndRecordAccepted(queue, item);
                if (!enqueued)
                {
                    workerPool.UntrackEnqueued(item.Id);
                    await requestRepo.DeleteRequestAsync(item.Id, conn, httpContext.RequestAborted);
                    return ProblemDetailsMapper.CreateProblemResult(
                        httpContext, opts,
                        StatusCodes.Status503ServiceUnavailable,
                        "Сервіс завершує роботу",
                        "Черга закрита для прийому нових запитів.",
                        "queue_closed");
                }

                slotConsumedByQueue = true;

                var response = new EnrollmentAcceptedResponseDto(
                    item.Id,
                    item.StudentId,
                    item.GroupId,
                    "queued",
                    item.CreatedAtUtc);

                return Results.Accepted($"/v1/enrollment-requests/{item.Id}", response);
            }
            finally
            {
                if (!slotConsumedByQueue)
                {
                    queue.ReleaseSlot();
                    if (item != null)
                    {
                        workerPool.UntrackEnqueued(item.Id);
                    }
                }
            }
        });

        app.MapGet("/v1/enrollment-requests/{id}", async (
            string id,
            HttpContext ctx,
            MetricsCollector metrics,
            EnrollmentRequestRepository requestRepo,
            ServiceOptions opts) =>
        {
            if (!Guid.TryParse(id, out var parsedId))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status400BadRequest,
                    "Некоректний формат ідентифікатора заявки",
                    $"Значення '{id}' не є валідним GUID. Очікується формат UUID.",
                    "invalid_request_id_format");
            }

            var status = await requestRepo.MarkFirstReadAndGetStatusAsync(parsedId, DateTimeOffset.UtcNow, ctx.RequestAborted);

            if (status == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status404NotFound,
                    "Заявку не знайдено",
                    $"Заявка з ідентифікатором '{parsedId}' відсутня в базі даних.",
                    "request_not_found");
            }

            // О1.5 DPC: час першої видачі кінцевого результату фіксується ТІЛЬКИ для вже завершених заявок
            if (status.Status is "done" or "rejected" or "failed")
            {
                metrics.RecordFirstReadIfCompleted(parsedId);
            }

            var (qWait, proc, deliv) = metrics.GetItemTimings(parsedId);

            return Results.Ok(status with
            {
                QueueWaitMs = qWait,
                ProcessingTimeMs = proc,
                DeliveryWaitMs = deliv
            });
        });

        return app;
    }
}
