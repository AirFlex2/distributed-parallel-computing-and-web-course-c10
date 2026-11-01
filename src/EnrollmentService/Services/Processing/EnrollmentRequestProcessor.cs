using EnrollmentService.Domain;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;

namespace EnrollmentService.Services.Processing;

public sealed class EnrollmentRequestProcessor
{
    private readonly EnrollmentRequestRepository _requestRepo;
    private readonly GroupRepository _groupRepo;
    private readonly EnrollmentRepository _enrollmentRepo;
    private readonly DecanateRepository _decanateRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly MetricsCollector _metrics;
    private readonly ServiceOptions _options;

    public EnrollmentRequestProcessor(
        EnrollmentRequestRepository requestRepo,
        GroupRepository groupRepo,
        EnrollmentRepository enrollmentRepo,
        DecanateRepository decanateRepo,
        IDbConnectionFactory connectionFactory,
        MetricsCollector metrics,
        ServiceOptions options)
    {
        _requestRepo = requestRepo;
        _groupRepo = groupRepo;
        _enrollmentRepo = enrollmentRepo;
        _decanateRepo = decanateRepo;
        _connectionFactory = connectionFactory;
        _metrics = metrics;
        _options = options;
    }

    public async Task ProcessItemAsync(
        int workerId,
        QueuedRequestItem item,
        bool isAsyncModel,
        IBoundedRequestQueue queue,
        CancellationToken cancellationToken = default)
    {
        _metrics.RecordWorkerStarted(item, queue.Count);
        var startedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            await _requestRepo.MarkStartedAsync(item.Id, startedAtUtc, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _metrics.RecordWorkerFinished(item, "failed", queue.Count);
            throw;
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_mark_started_failed", new Dictionary<string, object?>
            {
                ["worker_id"] = workerId,
                ["request_id"] = item.Id,
                ["error"] = ex.Message
            });
        }

        string finalStatus = "failed";
        string? rejectionReason = null;

        try
        {
            bool isUnsafe = _options.AllowTestOverrides && _options.UnsafeMode;
            // Єдиний конвеєр обробки для SAFE та UNSAFE режимів (О1.4 РПО):
            // Єдина відмінність між ними — наявність транзакційного блокування FOR UPDATE у SAFE.
            (finalStatus, rejectionReason) = await ProcessEnrollmentCoreAsync(item, isUnsafe, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            finalStatus = "failed";
            rejectionReason = "Обробку перервано через зупинку процесу (SIGTERM/Cancellation).";
        }
        catch (Exception ex)
        {
            finalStatus = "failed";
            rejectionReason = $"Системна помилка під час обробки: {ex.Message}";
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_processing_exception", new Dictionary<string, object?>
            {
                ["worker_id"] = workerId,
                ["request_id"] = item.Id,
                ["error"] = ex.ToString()
            });
        }

        try
        {
            var finishedAtUtc = _metrics.MarkProcessingFinished(item);

            // П. 8: Якщо це не done або це UNSAFE режим, записуємо статус у БД
            // (Для SAFE режиму 'done' вже зафіксовано атомарно всередині бізнес-транзакції tx)
            if (finalStatus != "done" || (_options.AllowTestOverrides && _options.UnsafeMode))
            {
                bool updated = false;
                for (int attempt = 1; attempt <= 3 && !updated; attempt++)
                {
                    try
                    {
                        await using var conn = await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
                        await _requestRepo.UpdateStatusAsync(
                            item.Id,
                            finalStatus,
                            rejectionReason,
                            finalStatus == "done" ? item.GroupId : null,
                            finishedAtUtc,
                            conn,
                            tx: null,
                            CancellationToken.None);
                        updated = true;
                    }
                    catch (Exception ex)
                    {
                        StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_status_retry_failed", new Dictionary<string, object?>
                        {
                            ["request_id"] = item.Id,
                            ["attempt"] = attempt,
                            ["error"] = ex.Message
                        });
                        await Task.Delay(50 * attempt);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_status_update_failed", new Dictionary<string, object?>
            {
                ["request_id"] = item.Id,
                ["error"] = ex.Message
            });
        }
        finally
        {
            _metrics.RecordWorkerFinished(item, finalStatus, queue.Count);
        }

        StructuredJsonLogger.LogEvent(_options.InstanceId, "worker_processed_item", new Dictionary<string, object?>
        {
            ["worker_id"] = workerId,
            ["model"] = isAsyncModel ? "async_tasks" : "dedicated_threads",
            ["request_id"] = item.Id,
            ["student_id"] = item.StudentId,
            ["group_id"] = item.GroupId,
            ["status"] = finalStatus,
            ["rejection_reason"] = rejectionReason
        });
    }

    private async Task<(string Status, string? Reason)> ProcessEnrollmentCoreAsync(
        QueuedRequestItem item,
        bool isUnsafe,
        CancellationToken cancellationToken)
    {
        if (!isUnsafe)
        {
            // SAFE: повільний зовнішній виклик (симуляція деканату 200–700 мс) виконується
            // СТРОГО ПОЗА критичною секцією (до відкриття транзакції та взяття блокувань БД)
            int slowDelayMs = Random.Shared.Next(_options.SlowCallMinMs, _options.SlowCallMaxMs + 1);
            await Task.Delay(slowDelayMs, cancellationToken);
        }

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = isUnsafe ? null : await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Канонічний порядок блокувань для виключення взаємоблокувань (deadlocks):
            // 1. student FOR UPDATE (у SAFE режимі) або звичайний SELECT (в UNSAFE)
            var student = isUnsafe
                ? await _enrollmentRepo.GetStudentByIdAsync(item.StudentId, cancellationToken)
                : await _enrollmentRepo.GetStudentForUpdateAsync(item.StudentId, conn, tx!, cancellationToken);

            if (student == null)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ("rejected", $"Студента '{item.StudentId}' не знайдено в базі даних.");
            }

            // 2. discipline_group FOR UPDATE (у SAFE режимі) або звичайний SELECT (в UNSAFE)
            var groupWithDisc = await _groupRepo.GetGroupWithDisciplineAsync(
                item.GroupId,
                conn,
                tx,
                forUpdate: !isUnsafe,
                cancellationToken);

            if (!groupWithDisc.HasValue)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ("failed", $"Навчальну групу '{item.GroupId}' не знайдено.");
            }

            var group = groupWithDisc.Value.Group;
            string disciplineId = group.DisciplineId;
            decimal disciplineEcts = groupWithDisc.Value.DisciplineEcts;

            // 3. Перевірка ідемпотентності за request_id
            string? existingRosterGroup = await _decanateRepo.GetRosterGroupForRequestAsync(item.Id, conn, tx, cancellationToken);
            if (existingRosterGroup != null)
            {
                if (tx != null)
                {
                    await _requestRepo.UpdateStatusAsync(
                        item.Id,
                        "done",
                        null,
                        existingRosterGroup,
                        DateTimeOffset.UtcNow,
                        conn,
                        tx,
                        cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }
                return ("done", null);
            }

            // 4. Перевірка наявності активного запису на дисципліну в обох таблицях (єдине предметне правило §1.10)
            bool alreadyEnrolled = await _enrollmentRepo.HasActiveEnrollmentForDisciplineAsync(item.StudentId, disciplineId, conn, tx, cancellationToken);

            var policyResult = EnrollmentPolicy.Check(
                student,
                group,
                disciplineEcts,
                DateTimeOffset.UtcNow,
                alreadyEnrolled);

            if (!policyResult.Allowed)
            {
                if (tx != null) await tx.RollbackAsync(cancellationToken);
                return ("rejected", policyResult.RejectionReason);
            }

            if (isUnsafe)
            {
                // UNSAFE: штучне часове вікно гонитви TOCTOU між перевіркою та списанням місць без блокування
                int delayMs = Random.Shared.Next(_options.SlowCallMinMs, _options.SlowCallMaxMs + 1);
                await Task.Delay(delayMs, cancellationToken);
            }

            if (!isUnsafe)
            {
                // БЕЗПЕЧНИЙ РЕЖИМ (SAFE): атомарний декремент available та відкат у разі браку місць
                bool decremented = await _groupRepo.DecrementAvailableAsync(item.GroupId, conn, tx, cancellationToken);
                if (!decremented)
                {
                    await tx!.RollbackAsync(cancellationToken);
                    return ("rejected", $"У навчальній групі '{item.GroupId}' немає вільних місць (декремент available повернув false).");
                }

                // Оновлення кількості та кредитів студента
                await _enrollmentRepo.UpdateStudentStatsAsync(item.StudentId, +1, disciplineEcts, conn, tx, cancellationToken);

                var finishedAtUtc = DateTimeOffset.UtcNow;

                // Запис до відомості деканату
                await _decanateRepo.InsertRosterEntryAsync(
                    item.Id,
                    item.StudentId,
                    item.GroupId,
                    disciplineId,
                    disciplineEcts,
                    null,
                    "primary_roster",
                    finishedAtUtc,
                    conn,
                    tx,
                    cancellationToken);

                // Запис кінцевого стану 'done' у тій самій транзакції, що і предметний ефект
                await _requestRepo.UpdateStatusAsync(
                    item.Id,
                    "done",
                    null,
                    item.GroupId,
                    finishedAtUtc,
                    conn,
                    tx,
                    cancellationToken);

                await tx!.CommitAsync(cancellationToken);
                return ("done", null);
            }
            else
            {
                // НЕБЕЗПЕЧНИЙ РЕЖИМ (UNSAFE): відсутність транзакційного блокування FOR UPDATE.
                // Всі конкурентні запити одночасно пройшли перевірку policyResult.Allowed
                // і фіксують студентів у відомості деканату, перевищуючи ліміт місць (стан гонитви TOCTOU).
                await _groupRepo.DecrementAvailableAsync(item.GroupId, conn, null, cancellationToken);
                await _enrollmentRepo.UpdateStudentStatsAsync(item.StudentId, +1, disciplineEcts, conn, null, cancellationToken);

                var finishedAtUtc = DateTimeOffset.UtcNow;
                await _decanateRepo.InsertRosterEntryAsync(
                    item.Id,
                    item.StudentId,
                    item.GroupId,
                    disciplineId,
                    disciplineEcts,
                    null,
                    "unsafe_overcommit",
                    finishedAtUtc,
                    conn,
                    null,
                    cancellationToken);

                return ("done", null);
            }
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
