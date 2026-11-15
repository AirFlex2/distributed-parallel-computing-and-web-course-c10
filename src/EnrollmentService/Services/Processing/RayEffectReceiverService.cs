using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using EnrollmentService.Domain;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using Npgsql;

namespace EnrollmentService.Services.Processing;

public sealed record RayEffectApplyRequest(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("student_id")] string StudentId,
    [property: JsonPropertyName("group_id")] string GroupId,
    [property: JsonPropertyName("enrollment_id")] Guid? EnrollmentId = null,
    [property: JsonPropertyName("requested_enrollment_id")] Guid? RequestedEnrollmentId = null,
    [property: JsonPropertyName("is_unsafe")] bool IsUnsafe = false,
    [property: JsonPropertyName("worker_id")] string? WorkerId = null,
    [property: JsonPropertyName("ray_attempt")] int RayAttempt = 1
);

public sealed record RayEffectApplyResponse(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("rejection_reason")] string? RejectionReason,
    [property: JsonPropertyName("assigned_group_id")] string? AssignedGroupId,
    [property: JsonPropertyName("roster_recorded")] bool RosterRecorded,
    [property: JsonPropertyName("already_applied")] bool AlreadyApplied,
    [property: JsonPropertyName("delivery_count")] int DeliveryCount
);

/// <summary>
/// Приймач результатів Ray (РПО ЛР3: О3.1, О3.3, О3.4).
/// Єдиний компонент, який атомарно в одній транзакції PostgreSQL перевіряє журнал застосованих ефектів dpc_applied_effects,
/// застосовує доменний ефект (decanate_roster + discipline_group) та фіксує рішення за ключем request_id.
/// </summary>
public sealed class RayEffectReceiverService
{
    private readonly EnrollmentRequestRepository _requestRepo;
    private readonly GroupRepository _groupRepo;
    private readonly EnrollmentRepository _enrollmentRepo;
    private readonly DecanateRepository _decanateRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly MetricsCollector _metrics;
    private readonly ServiceOptions _options;
    private readonly IWebEnrollmentNotifier _webNotifier;

    public ConcurrentDictionary<Guid, Func<QueuedRequestItem, Task>> ScopedPostCommitBeforeNotifyHooks { get; } = new();
    public Func<QueuedRequestItem, Task>? TestHookAfterCommitAsync { get; set; }

    public RayEffectReceiverService(
        EnrollmentRequestRepository requestRepo,
        GroupRepository groupRepo,
        EnrollmentRepository enrollmentRepo,
        DecanateRepository decanateRepo,
        IDbConnectionFactory connectionFactory,
        MetricsCollector metrics,
        ServiceOptions options,
        IWebEnrollmentNotifier webNotifier)
    {
        _requestRepo = requestRepo;
        _groupRepo = groupRepo;
        _enrollmentRepo = enrollmentRepo;
        _decanateRepo = decanateRepo;
        _connectionFactory = connectionFactory;
        _metrics = metrics;
        _options = options;
        _webNotifier = webNotifier;
    }

    public async Task<RayEffectApplyResponse> ApplyEffectAsync(
        RayEffectApplyRequest req,
        QueuedRequestItem? trackedItem = null,
        CancellationToken cancellationToken = default)
    {
        bool isUnsafe = req.IsUnsafe && _options.AllowTestOverrides;
        Guid? targetEnrollmentId = req.RequestedEnrollmentId ?? req.EnrollmentId;

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        bool committed = false;

        try
        {
            // Серіалізуємо конкурентну або повторну доставку одного і того самого request_id
            await using (var lockCmd = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(202633, hashtext(@reqIdText));", conn, tx))
            {
                lockCmd.Parameters.AddWithValue("reqIdText", req.RequestId.ToString("D"));
                await lockCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 1. О3.3 / О3.4: Перевіряємо таблицю застосованих ефектів dpc_applied_effects за ключем request_id
            var existingEffect = await GetAppliedEffectForUpdateAsync(req.RequestId, conn, tx, cancellationToken);
            if (existingEffect != null)
            {
                int nextDeliveryCount = existingEffect.DeliveryCount + 1;
                await using (var updEffCmd = new NpgsqlCommand(@"
                    UPDATE dpc_applied_effects
                    SET delivery_count = @deliveryCount,
                        ray_attempt = GREATEST(ray_attempt, @rayAttempt),
                        worker_id = COALESCE(@workerId, worker_id),
                        updated_at = NOW()
                    WHERE request_id = @reqId;", conn, tx))
                {
                    updEffCmd.Parameters.AddWithValue("deliveryCount", nextDeliveryCount);
                    updEffCmd.Parameters.AddWithValue("rayAttempt", Math.Max(1, req.RayAttempt));
                    updEffCmd.Parameters.Add(new NpgsqlParameter("workerId", NpgsqlTypes.NpgsqlDbType.Text)
                    {
                        Value = (object?)req.WorkerId ?? DBNull.Value
                    });
                    updEffCmd.Parameters.AddWithValue("reqId", req.RequestId);
                    await updEffCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await _requestRepo.UpdateStatusAsync(
                    req.RequestId,
                    existingEffect.Status,
                    existingEffect.RejectionReason,
                    existingEffect.AssignedGroupId,
                    DateTimeOffset.UtcNow,
                    conn,
                    tx,
                    cancellationToken);

                await tx.CommitAsync(cancellationToken);
                committed = true;

                StructuredJsonLogger.LogEvent(_options.InstanceId, "ray.effect.duplicate_ignored", new Dictionary<string, object?>
                {
                    ["request_id"] = req.RequestId,
                    ["status"] = existingEffect.Status,
                    ["delivery_count"] = nextDeliveryCount,
                    ["ray_attempt"] = req.RayAttempt,
                    ["worker_id"] = req.WorkerId
                });

                if (existingEffect.Status == "done" && targetEnrollmentId.HasValue)
                {
                    await TryNotifyWebAsync(targetEnrollmentId.Value, req.RequestId, cancellationToken);
                }

                return new RayEffectApplyResponse(
                    req.RequestId,
                    existingEffect.Status,
                    existingEffect.RejectionReason,
                    existingEffect.AssignedGroupId,
                    existingEffect.RosterRecorded,
                    AlreadyApplied: true,
                    DeliveryCount: nextDeliveryCount);
            }

            // 2. Зворотна перевірка decanate_roster (якщо запис вже внесено у відомість)
            string? existingRosterGroup = await _decanateRepo.GetRosterGroupForRequestAsync(req.RequestId, conn, tx, cancellationToken);
            if (existingRosterGroup != null)
            {
                var finishedAtUtc = DateTimeOffset.UtcNow;
                await InsertAppliedEffectAsync(
                    req.RequestId, req.StudentId, req.GroupId, "done", null, existingRosterGroup, true, req.RayAttempt, req.WorkerId, conn, tx, cancellationToken);
                await _requestRepo.UpdateStatusAsync(
                    req.RequestId, "done", null, existingRosterGroup, finishedAtUtc, conn, tx, cancellationToken);

                await tx.CommitAsync(cancellationToken);
                committed = true;
                if (trackedItem != null) _metrics.MarkProcessingFinished(trackedItem);

                if (targetEnrollmentId.HasValue)
                {
                    await TryNotifyWebAsync(targetEnrollmentId.Value, req.RequestId, cancellationToken);
                }

                return new RayEffectApplyResponse(req.RequestId, "done", null, existingRosterGroup, true, AlreadyApplied: true, DeliveryCount: 2);
            }

            // 3. Блокування в канонічному порядку: student -> discipline_group
            var student = isUnsafe
                ? await _enrollmentRepo.GetStudentByIdAsync(req.StudentId, cancellationToken)
                : await _enrollmentRepo.GetStudentForUpdateAsync(req.StudentId, conn, tx, cancellationToken);

            if (student == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return await RecordRejectedDecisionAsync(req, $"Студента '{req.StudentId}' не знайдено в базі даних.", cancellationToken);
            }

            var groupWithDisc = await _groupRepo.GetGroupWithDisciplineAsync(
                req.GroupId, conn, tx, forUpdate: !isUnsafe, cancellationToken);

            if (!groupWithDisc.HasValue)
            {
                await tx.RollbackAsync(cancellationToken);
                return await RecordFailedOrRejectedWithoutEffectAsync(req, "failed", $"Навчальну групу '{req.GroupId}' не знайдено.");
            }

            var group = groupWithDisc.Value.Group;

            if (!group.DpcSandboxEnabled && !targetEnrollmentId.HasValue)
            {
                await tx.RollbackAsync(cancellationToken);
                return await RecordRejectedDecisionAsync(
                    req,
                    $"Група '{req.GroupId}' не призначена для автономної обробки РПО (dpc_sandbox_enabled = false).",
                    cancellationToken);
            }

            if (isUnsafe && targetEnrollmentId.HasValue)
            {
                await tx.RollbackAsync(cancellationToken);
                return await RecordRejectedDecisionAsync(
                    req,
                    "Спільний наскрізний сценарій Web + DPC підтримується виключно в безпечному режимі SAFE (атомарна транзакція з блокуванням FOR UPDATE).",
                    cancellationToken);
            }

            string disciplineId = group.DisciplineId;
            decimal disciplineEcts = groupWithDisc.Value.DisciplineEcts;

            if (!isUnsafe)
            {
                // Спільний сценарій Web + DPC
                if (targetEnrollmentId.HasValue)
                {
                    var heldEnrollment = await _enrollmentRepo.GetEnrollmentForJointVerificationAsync(
                        targetEnrollmentId.Value, conn, tx, cancellationToken);

                    if (!heldEnrollment.HasValue)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return await RecordRejectedDecisionAsync(
                            req, $"Пов'язаний Web-запис '{targetEnrollmentId.Value}' не знайдено або було видалено з бази даних.", cancellationToken);
                    }

                    if (!string.Equals(heldEnrollment.Value.StudentId, req.StudentId, StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return await RecordRejectedDecisionAsync(
                            req, $"Web-запис '{targetEnrollmentId.Value}' належить іншому студенту ('{heldEnrollment.Value.StudentId}', очікувався '{req.StudentId}').", cancellationToken);
                    }

                    if (!string.Equals(heldEnrollment.Value.GroupId, req.GroupId, StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return await RecordRejectedDecisionAsync(
                            req, $"Web-запис '{targetEnrollmentId.Value}' відноситься до групи '{heldEnrollment.Value.GroupId}', а не '{req.GroupId}'.", cancellationToken);
                    }

                    if (!string.Equals(heldEnrollment.Value.Status, "held", StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return await RecordRejectedDecisionAsync(
                            req, $"Web-запис '{targetEnrollmentId.Value}' перебуває у неприпустимому для підтвердження стані '{heldEnrollment.Value.Status}'.", cancellationToken);
                    }

                    if (heldEnrollment.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return await RecordRejectedDecisionAsync(
                            req, $"Термін дії резервування Web-запису '{targetEnrollmentId.Value}' сплив.", cancellationToken);
                    }

                    if (group.Closed)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return await RecordRejectedDecisionAsync(
                            req, $"Навчальна група '{group.Id}' закрита працівником деканату (closed = true).", cancellationToken);
                    }

                    var finishedAtUtcJoint = DateTimeOffset.UtcNow;

                    try
                    {
                        await _decanateRepo.InsertRosterEntryAsync(
                            req.RequestId, req.StudentId, req.GroupId, disciplineId, disciplineEcts,
                            targetEnrollmentId.Value, "joint_web_enrollment", finishedAtUtcJoint, conn, tx, cancellationToken);
                    }
                    catch (DecanateConflictException ex)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return await RecordRejectedDecisionAsync(req, ex.Message, cancellationToken);
                    }

                    await InsertAppliedEffectAsync(
                        req.RequestId, req.StudentId, req.GroupId, "done", null, req.GroupId, true, req.RayAttempt, req.WorkerId, conn, tx, cancellationToken);
                    await _requestRepo.UpdateStatusAsync(
                        req.RequestId, "done", null, req.GroupId, finishedAtUtcJoint, conn, tx, cancellationToken);

                    await tx.CommitAsync(cancellationToken);
                    committed = true;

                    if (trackedItem != null) _metrics.MarkProcessingFinished(trackedItem);
                    await RunPostCommitHooksAsync(req.RequestId, trackedItem);
                    await TryNotifyWebAsync(targetEnrollmentId.Value, req.RequestId, cancellationToken);

                    return new RayEffectApplyResponse(req.RequestId, "done", null, req.GroupId, true, AlreadyApplied: false, DeliveryCount: 1);
                }

                // Автономний прийом (SAFE mode)
                bool alreadyEnrolled = await _enrollmentRepo.HasActiveEnrollmentForDisciplineAsync(req.StudentId, disciplineId, conn, tx, cancellationToken);
                var policyResult = EnrollmentPolicy.Check(student, group, disciplineEcts, DateTimeOffset.UtcNow, alreadyEnrolled);
                if (!policyResult.Allowed)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return await RecordRejectedDecisionAsync(req, policyResult.RejectionReason ?? "Відхилено правилами запису.", cancellationToken);
                }

                bool decremented = await _groupRepo.DecrementAvailableAsync(req.GroupId, conn, tx, cancellationToken);
                if (!decremented)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return await RecordRejectedDecisionAsync(
                        req, $"У навчальній групі '{req.GroupId}' немає вільних місць (декремент available повернув false).", cancellationToken);
                }

                await _enrollmentRepo.UpdateStudentStatsAsync(req.StudentId, +1, disciplineEcts, conn, tx, cancellationToken);
                var finishedAtUtc = DateTimeOffset.UtcNow;

                try
                {
                    await _decanateRepo.InsertRosterEntryAsync(
                        req.RequestId, req.StudentId, req.GroupId, disciplineId, disciplineEcts,
                        null, "primary_roster", finishedAtUtc, conn, tx, cancellationToken);
                }
                catch (DecanateConflictException ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return await RecordRejectedDecisionAsync(req, ex.Message, cancellationToken);
                }

                await InsertAppliedEffectAsync(
                    req.RequestId, req.StudentId, req.GroupId, "done", null, req.GroupId, true, req.RayAttempt, req.WorkerId, conn, tx, cancellationToken);
                await _requestRepo.UpdateStatusAsync(
                    req.RequestId, "done", null, req.GroupId, finishedAtUtc, conn, tx, cancellationToken);

                await tx.CommitAsync(cancellationToken);
                committed = true;

                if (trackedItem != null) _metrics.MarkProcessingFinished(trackedItem);
                await RunPostCommitHooksAsync(req.RequestId, trackedItem);

                return new RayEffectApplyResponse(req.RequestId, "done", null, req.GroupId, true, AlreadyApplied: false, DeliveryCount: 1);
            }
            else
            {
                // UNSAFE режим (тільки для демонстрації Lost Update ЛР1 О1.4)
                bool alreadyEnrolled = await _enrollmentRepo.HasActiveEnrollmentForDisciplineAsync(req.StudentId, disciplineId, conn, tx, cancellationToken);
                var policyResult = EnrollmentPolicy.Check(student, group, disciplineEcts, DateTimeOffset.UtcNow, alreadyEnrolled);
                if (!policyResult.Allowed)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return await RecordRejectedDecisionAsync(req, policyResult.RejectionReason ?? "Відхилено правилами запису.", cancellationToken);
                }

                int newAvailable = group.Available - 1;
                await _groupRepo.SetAvailableUnsafeAsync(req.GroupId, newAvailable, conn, tx, cancellationToken);
                await _enrollmentRepo.UpdateStudentStatsAsync(req.StudentId, +1, disciplineEcts, conn, tx, cancellationToken);

                var finishedAtUtc = DateTimeOffset.UtcNow;
                try
                {
                    await _decanateRepo.InsertRosterEntryAsync(
                        req.RequestId, req.StudentId, req.GroupId, disciplineId, disciplineEcts,
                        null, "unsafe_overcommit", finishedAtUtc, conn, tx, cancellationToken);
                }
                catch (DecanateConflictException ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return await RecordRejectedDecisionAsync(req, ex.Message, cancellationToken);
                }

                await InsertAppliedEffectAsync(
                    req.RequestId, req.StudentId, req.GroupId, "done", null, req.GroupId, true, req.RayAttempt, req.WorkerId, conn, tx, cancellationToken);
                await _requestRepo.UpdateStatusAsync(
                    req.RequestId, "done", null, req.GroupId, finishedAtUtc, conn, tx, cancellationToken);

                await tx.CommitAsync(cancellationToken);
                committed = true;

                if (trackedItem != null) _metrics.MarkProcessingFinished(trackedItem);
                await RunPostCommitHooksAsync(req.RequestId, trackedItem);

                return new RayEffectApplyResponse(req.RequestId, "done", null, req.GroupId, true, AlreadyApplied: false, DeliveryCount: 1);
            }
        }
        catch
        {
            if (!committed)
            {
                if (trackedItem != null) _metrics.ResetProcessingFinished(trackedItem);
                try { await tx.RollbackAsync(cancellationToken); } catch { }
            }
            throw;
        }
    }

    private async Task<RayEffectApplyResponse> RecordRejectedDecisionAsync(
        RayEffectApplyRequest req,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var lockCmd = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(202633, hashtext(@reqIdText));", conn, tx))
            {
                lockCmd.Parameters.AddWithValue("reqIdText", req.RequestId.ToString("D"));
                await lockCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var existing = await GetAppliedEffectForUpdateAsync(req.RequestId, conn, tx, cancellationToken);
            if (existing != null)
            {
                await tx.CommitAsync(cancellationToken);
                return new RayEffectApplyResponse(
                    req.RequestId,
                    existing.Status,
                    existing.RejectionReason,
                    existing.AssignedGroupId,
                    existing.RosterRecorded,
                    AlreadyApplied: true,
                    DeliveryCount: existing.DeliveryCount);
            }

            var finishedAt = DateTimeOffset.UtcNow;
            await InsertAppliedEffectAsync(
                req.RequestId, req.StudentId, req.GroupId, "rejected", reason, null, false, req.RayAttempt, req.WorkerId, conn, tx, cancellationToken);
            await _requestRepo.UpdateStatusAsync(
                req.RequestId, "rejected", reason, null, finishedAt, conn, tx, cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new RayEffectApplyResponse(
                req.RequestId, "rejected", reason, null, false, AlreadyApplied: false, DeliveryCount: 1);
        }
        catch
        {
            try { await tx.RollbackAsync(cancellationToken); } catch { }
            throw;
        }
    }

    private static Task<RayEffectApplyResponse> RecordFailedOrRejectedWithoutEffectAsync(
        RayEffectApplyRequest req,
        string status,
        string reason)
    {
        return Task.FromResult(new RayEffectApplyResponse(
            req.RequestId, status, reason, null, false, AlreadyApplied: false, DeliveryCount: 1));
    }

    private async Task RunPostCommitHooksAsync(Guid requestId, QueuedRequestItem? trackedItem)
    {
        if (trackedItem != null && _options.AllowTestOverrides && TestHookAfterCommitAsync != null)
        {
            try
            {
                await TestHookAfterCommitAsync(trackedItem);
            }
            catch (Exception hookEx)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "test_hook_after_commit_exception", new Dictionary<string, object?>
                {
                    ["request_id"] = requestId,
                    ["error"] = hookEx.ToString()
                });
            }
        }

        if (trackedItem != null && ScopedPostCommitBeforeNotifyHooks.TryGetValue(requestId, out var postCommitHook))
        {
            try
            {
                await postCommitHook(trackedItem);
            }
            catch (Exception hookEx)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "scoped_post_commit_hook_exception", new Dictionary<string, object?>
                {
                    ["request_id"] = requestId,
                    ["error"] = hookEx.ToString()
                });
            }
        }
    }

    private async Task TryNotifyWebAsync(Guid enrollmentId, Guid requestId, CancellationToken cancellationToken)
    {
        try
        {
            await _webNotifier.NotifyRosterRecordedAsync(enrollmentId, requestId, cancellationToken);
        }
        catch (Exception notifyEx)
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "web_notification_failed", new Dictionary<string, object?>
            {
                ["request_id"] = requestId,
                ["enrollment_id"] = enrollmentId,
                ["error"] = notifyEx.Message
            });
        }
    }

    private sealed record AppliedEffectRecord(
        string Status,
        string? RejectionReason,
        string? AssignedGroupId,
        bool RosterRecorded,
        int DeliveryCount
    );

    private static async Task<AppliedEffectRecord?> GetAppliedEffectForUpdateAsync(
        Guid requestId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT status, rejection_reason, assigned_group_id, roster_recorded, delivery_count
            FROM dpc_applied_effects
            WHERE request_id = @reqId
            FOR UPDATE;", conn, tx);
        cmd.Parameters.AddWithValue("reqId", requestId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AppliedEffectRecord(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3),
            reader.GetInt32(4));
    }

    private static async Task InsertAppliedEffectAsync(
        Guid requestId,
        string studentId,
        string groupId,
        string status,
        string? rejectionReason,
        string? assignedGroupId,
        bool rosterRecorded,
        int rayAttempt,
        string? workerId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO dpc_applied_effects (
                request_id, student_id, group_id, status, rejection_reason, assigned_group_id,
                roster_recorded, delivery_count, ray_attempt, worker_id, created_at, updated_at
            ) VALUES (
                @reqId, @studentId, @groupId, @status, @rejectionReason, @assignedGroupId,
                @rosterRecorded, 1, @rayAttempt, @workerId, NOW(), NOW()
            )
            ON CONFLICT (request_id) DO NOTHING;", conn, tx);
        cmd.Parameters.AddWithValue("reqId", requestId);
        cmd.Parameters.AddWithValue("studentId", studentId);
        cmd.Parameters.AddWithValue("groupId", groupId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.Add(new NpgsqlParameter("rejectionReason", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)rejectionReason ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("assignedGroupId", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)assignedGroupId ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("rosterRecorded", rosterRecorded);
        cmd.Parameters.AddWithValue("rayAttempt", Math.Max(1, rayAttempt));
        cmd.Parameters.Add(new NpgsqlParameter("workerId", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)workerId ?? DBNull.Value
        });

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
