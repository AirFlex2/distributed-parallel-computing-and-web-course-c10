using System.Diagnostics;
using System.Globalization;
using EnrollmentService.Http;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Routing;

namespace EnrollmentService.Endpoints;

public static class EnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Довідковий маршрут: список студентів у БД із захистом персональних даних (Web О1.4, О3.2)
        // Примітка: заголовок X-User-Id є навчальною симуляцією контексту користувача для ЛР1 (до впровадження JWT у ЛР3);
        // повноцінна автентифікація та авторизація персоналу реалізована через заголовок Authorization: Bearer <token>.
        // 401 Unauthorized — якщо немає Bearer токена або X-User-Id
        // 403 Forbidden — якщо студент із X-User-Id намагається переглянути чужі дані через ?student_id=...
        // 200 OK — персонал бачить усіх або фільтрованого студента; студент бачить виключно власний профіль
        app.MapGet("/v1/students", async (EnrollmentRepository enrollmentRepo, HttpContext ctx, ServiceOptions opts) =>
        {
            string authHeader = ctx.Request.Headers.Authorization.ToString().Trim();
            bool isStaff = !string.IsNullOrWhiteSpace(authHeader) &&
                string.Equals(authHeader, $"Bearer {opts.ApiAuthToken}", StringComparison.Ordinal);

            bool hasStudentHeader = ctx.Request.Headers.TryGetValue("X-User-Id", out var userHeader) &&
                !string.IsNullOrWhiteSpace(userHeader.ToString());
            string callerStudentId = hasStudentHeader ? userHeader.ToString().Trim() : string.Empty;

            if (!isStaff && !hasStudentHeader)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status401Unauthorized,
                    "Неавторизований доступ",
                    "Для доступу до персональних даних студентів необхідна автентифікація (Bearer токен персоналу або заголовок X-User-Id студента).",
                    "unauthorized");
            }

            if (!isStaff)
            {
                if (ctx.Request.Query.TryGetValue("student_id", out var qStudent) && !string.IsNullOrWhiteSpace(qStudent))
                {
                    if (!string.Equals(qStudent.ToString().Trim(), callerStudentId, StringComparison.Ordinal))
                    {
                        return ProblemDetailsMapper.CreateProblemResult(
                            ctx, opts,
                            StatusCodes.Status403Forbidden,
                            "Доступ заборонено",
                            "Студент не має права переглядати персональні дані інших студентів.",
                            "forbidden");
                    }
                }

                var self = await enrollmentRepo.GetStudentByIdAsync(callerStudentId, ctx.RequestAborted);
                var selfItems = self != null
                    ? new[] { new WebStudentDto(self.Id, self.Email, self.FullName, self.MaxDisciplines, self.ActiveCount, self.ActiveEcts, EnrollmentDomainRules.MaxSemesterEcts) }
                    : Array.Empty<WebStudentDto>();
                return Results.Ok(new { items = selfItems });
            }

            if (ctx.Request.Query.TryGetValue("student_id", out var filterStudent) && !string.IsNullOrWhiteSpace(filterStudent))
            {
                var s = await enrollmentRepo.GetStudentByIdAsync(filterStudent.ToString().Trim(), ctx.RequestAborted);
                var items = s != null
                    ? new[] { new WebStudentDto(s.Id, s.Email, s.FullName, s.MaxDisciplines, s.ActiveCount, s.ActiveEcts, EnrollmentDomainRules.MaxSemesterEcts) }
                    : Array.Empty<WebStudentDto>();
                return Results.Ok(new { items });
            }

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

        // Отримання профілю конкретного студента за його ідентифікатором (Web О3.2)
        app.MapGet("/v1/students/{id}", async (string id, EnrollmentRepository enrollmentRepo, HttpContext ctx, ServiceOptions opts) =>
        {
            string authHeader = ctx.Request.Headers.Authorization.ToString().Trim();
            bool isStaff = !string.IsNullOrWhiteSpace(authHeader) &&
                string.Equals(authHeader, $"Bearer {opts.ApiAuthToken}", StringComparison.Ordinal);

            bool hasStudentHeader = ctx.Request.Headers.TryGetValue("X-User-Id", out var userHeader) &&
                !string.IsNullOrWhiteSpace(userHeader.ToString());
            string callerStudentId = hasStudentHeader ? userHeader.ToString().Trim() : string.Empty;

            if (!isStaff && !hasStudentHeader)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status401Unauthorized,
                    "Неавторизований доступ",
                    "Для доступу до персональних даних студента необхідна автентифікація.",
                    "unauthorized");
            }

            if (!isStaff && !string.Equals(id, callerStudentId, StringComparison.Ordinal))
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status403Forbidden,
                    "Доступ заборонено",
                    "Студент не має права переглядати персональні дані інших студентів.",
                    "forbidden");
            }

            var student = await enrollmentRepo.GetStudentByIdAsync(id, ctx.RequestAborted);
            if (student == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status404NotFound,
                    "Студента не знайдено",
                    $"Студент '{id}' не існує у системі.",
                    "student_not_found");
            }

            return Results.Ok(new WebStudentDto(
                student.Id,
                student.Email,
                student.FullName,
                student.MaxDisciplines,
                student.ActiveCount,
                student.ActiveEcts,
                EnrollmentDomainRules.MaxSemesterEcts));
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

        // Асинхронна реєстрація у відомості деканату через чергу РПО (Web С10, стор. 37: POST /v1/enrollments/{id}/registrations -> 202 Accepted)
        app.MapPost("/v1/enrollments/{id}/registrations", async (
            string id,
            HttpContext ctx,
            IBoundedRequestQueue queue,
            EnrollmentWorkerPool workerPool,
            MetricsCollector metrics,
            EnrollmentRequestRepository requestRepo,
            EnrollmentRepository enrollmentRepo,
            IDbConnectionFactory connectionFactory,
            ServiceOptions opts) =>
        {
            if (workerPool.IsShuttingDown)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status503ServiceUnavailable,
                    "Сервіс завершує роботу",
                    "Отримано сигнал SIGTERM (Д1.4). Нові запити не приймаються.",
                    "service_shutting_down");
            }

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

            string? idempotencyKey = null;
            if (ctx.Request.Headers.TryGetValue("Idempotency-Key", out var ikHeader) && !string.IsNullOrWhiteSpace(ikHeader))
            {
                idempotencyKey = ikHeader.ToString().Trim();
                if (idempotencyKey.Length > 128)
                {
                    return ProblemDetailsMapper.CreateProblemResult(
                        ctx, opts,
                        StatusCodes.Status400BadRequest,
                        "Некоректний заголовок Idempotency-Key",
                        "Довжина заголовка Idempotency-Key перевищує допустимий ліміт 128 символів.",
                        "invalid_idempotency_key");
                }

                // Перевірка ідемпотентності ДО резервування слота в черзі (пункт 1).
                // Працює як до взяття заявки в обробку, так і після завершення (done).
                await using var connCheck = await connectionFactory.OpenConnectionAsync(ctx.RequestAborted);
                var existing = await requestRepo.GetByIdempotencyKeyAsync(idempotencyKey, connCheck, ctx.RequestAborted);
                if (existing != null)
                {
                    Guid? existingEnrollmentId = existing.EnrollmentId ?? existing.RequestedEnrollmentId;
                    string expectedHash = $"{callerStudentId}:{existing.GroupId}:{enrollmentGuid}";

                    if (!string.Equals(existing.StudentId, callerStudentId, StringComparison.Ordinal) ||
                        existingEnrollmentId != enrollmentGuid ||
                        (existing.RequestPayloadHash != null &&
                         !string.Equals(existing.RequestPayloadHash, expectedHash, StringComparison.Ordinal)))
                    {
                        return ProblemDetailsMapper.CreateProblemResult(
                            ctx, opts,
                            StatusCodes.Status409Conflict,
                            "Конфлікт даних ідемпотентного запиту",
                            $"Ідемпотентний ключ '{idempotencyKey}' вже було використано з іншими параметрами.",
                            "idempotency_conflict");
                    }

                    string loc = $"/v1/enrollment-requests/{existing.Id}";
                    ctx.Response.Headers.Location = loc;
                    return Results.Accepted(loc, new EnrollmentAcceptedResponseDto(
                        existing.Id,
                        existing.StudentId,
                        existing.GroupId,
                        existing.Status,
                        existing.CreatedAt));
                }
            }

            var enrollment = await enrollmentRepo.GetEnrollmentByIdAsync(enrollmentGuid, callerStudentId, ctx.RequestAborted);
            if (enrollment == null)
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status404NotFound,
                    "Запис не знайдено",
                    $"Запис із номером '{enrollmentGuid}' для студента '{callerStudentId}' не існує.",
                    "enrollment_not_found");
            }

            if (enrollment.Status != "held")
            {
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status409Conflict,
                    "Запис не перебуває у стані held",
                    $"Неможливо зареєструвати у відомості запис зі статусом '{enrollment.Status}'. Очікується 'held'.",
                    "enrollment_not_held");
            }

            if (!queue.TryReserveSlot())
            {
                metrics.RecordRejected429();
                ctx.Response.Headers["Retry-After"] = "1";
                return ProblemDetailsMapper.CreateProblemResult(
                    ctx, opts,
                    StatusCodes.Status429TooManyRequests,
                    "Черга запитів переповнена (Backpressure)",
                    $"Перевищено місткість обмеженої черги QUEUE_CAPACITY ({opts.QueueCapacity}). Спробуйте повторити запит через 1 секунду.",
                    "queue_overflow");
            }

            bool slotConsumedByQueue = false;
            var requestId = Guid.NewGuid();
            string effectiveIdempotencyKey = !string.IsNullOrWhiteSpace(idempotencyKey)
                ? idempotencyKey
                : requestId.ToString("D");
            string payloadFingerprint = $"{callerStudentId}:{enrollment.GroupId}:{enrollmentGuid}";

            var item = new QueuedRequestItem
            {
                Id = requestId,
                StudentId = callerStudentId,
                GroupId = enrollment.GroupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                IdempotencyKey = effectiveIdempotencyKey,
                RequestPayloadHash = payloadFingerprint,
                EnrollmentId = enrollmentGuid,
                RequestedEnrollmentId = enrollmentGuid,
                EnqueueTimestamp = Stopwatch.GetTimestamp()
            };

            try
            {
                await using (var conn = await connectionFactory.OpenConnectionAsync(ctx.RequestAborted))
                {
                    bool inserted = await requestRepo.TryInsertQueuedRequestAsync(item, conn, ctx.RequestAborted);
                    if (!inserted)
                    {
                        // Гонка двох однакових запитів з одним Idempotency-Key
                        var existingAfterRace = await requestRepo.GetByIdempotencyKeyAsync(effectiveIdempotencyKey, conn, ctx.RequestAborted);
                        if (existingAfterRace != null)
                        {
                            Guid? exEnrollmentId = existingAfterRace.EnrollmentId ?? existingAfterRace.RequestedEnrollmentId;
                            if (string.Equals(existingAfterRace.StudentId, callerStudentId, StringComparison.Ordinal) &&
                                exEnrollmentId == enrollmentGuid &&
                                (existingAfterRace.RequestPayloadHash == null ||
                                 string.Equals(existingAfterRace.RequestPayloadHash, payloadFingerprint, StringComparison.Ordinal)))
                            {
                                string existingLoc = $"/v1/enrollment-requests/{existingAfterRace.Id}";
                                ctx.Response.Headers.Location = existingLoc;
                                return Results.Accepted(existingLoc, new EnrollmentAcceptedResponseDto(
                                    existingAfterRace.Id,
                                    existingAfterRace.StudentId,
                                    existingAfterRace.GroupId,
                                    existingAfterRace.Status,
                                    existingAfterRace.CreatedAt));
                            }

                            return ProblemDetailsMapper.CreateProblemResult(
                                ctx, opts,
                                StatusCodes.Status409Conflict,
                                "Конфлікт даних ідемпотентного запиту",
                                $"Ідемпотентний ключ '{effectiveIdempotencyKey}' вже було використано з іншими параметрами.",
                                "idempotency_conflict");
                        }

                        return ProblemDetailsMapper.CreateProblemResult(
                            ctx, opts,
                            StatusCodes.Status409Conflict,
                            "Конфлікт реєстрації",
                            "Заявку на реєстрацію вже було надіслано раніше.",
                            "registration_conflict");
                    }
                }

                workerPool.TrackEnqueued(item.Id);
                bool enqueued = metrics.TryEnqueueReservedAndRecordAccepted(queue, item);
                if (!enqueued)
                {
                    workerPool.UntrackEnqueued(item.Id);
                    await using (var conn = await connectionFactory.OpenConnectionAsync(CancellationToken.None))
                    {
                        await requestRepo.DeleteRequestAsync(item.Id, conn, CancellationToken.None);
                    }
                    metrics.RecordRejected429();
                    ctx.Response.Headers["Retry-After"] = "1";
                    return ProblemDetailsMapper.CreateProblemResult(
                        ctx, opts,
                        StatusCodes.Status429TooManyRequests,
                        "Черга запитів переповнена",
                        "Не вдалося розмістити заявку в черзі.",
                        "queue_overflow");
                }

                slotConsumedByQueue = true;

                string locUrl = $"/v1/enrollment-requests/{requestId}";
                ctx.Response.Headers.Location = locUrl;
                return Results.Accepted(locUrl, new EnrollmentAcceptedResponseDto(
                    requestId,
                    callerStudentId,
                    enrollment.GroupId,
                    "queued",
                    item.CreatedAtUtc));
            }
            finally
            {
                if (!slotConsumedByQueue)
                {
                    queue.ReleaseSlot();
                }
            }
        });

        return app;
    }
}
