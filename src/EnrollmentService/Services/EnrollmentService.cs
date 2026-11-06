using System.Globalization;
using System.Text;
using System.Text.Json;
using EnrollmentService.Domain;
using EnrollmentService.Repositories;
using Npgsql;

namespace EnrollmentService.Services;

public sealed class EnrollmentService : IWebEnrollmentConfirmationService
{
    private readonly GroupRepository _groupRepo;
    private readonly EnrollmentRepository _enrollmentRepo;
    private readonly DecanateRepository _decanateRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ServiceOptions _options;

    public EnrollmentService(
        GroupRepository groupRepo,
        EnrollmentRepository enrollmentRepo,
        DecanateRepository decanateRepo,
        IDbConnectionFactory connectionFactory,
        ServiceOptions options)
    {
        _groupRepo = groupRepo;
        _enrollmentRepo = enrollmentRepo;
        _decanateRepo = decanateRepo;
        _connectionFactory = connectionFactory;
        _options = options;
    }

    public async Task<ServiceResult<WebEnrollmentResponseDto>> CreateEnrollmentAsync(
        string studentId,
        CreateWebEnrollmentDto dto,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.GroupId) || dto.GroupId.Trim().Length > 64 || dto.GroupId.Contains(' '))
        {
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_group_id_format",
                "Некоректні дані запиту (Unprocessable Entity)",
                "Поле 'group_id' є обов'язковим, не може бути порожнім або містити пробіли.");
        }

        string groupId = dto.GroupId.Trim();
        string payloadFingerprint = EnrollmentDomainRules.ComputeWebEnrollmentFingerprint(
            studentId,
            groupId,
            EnrollmentDomainRules.CreateEnrollmentOperation);

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        // О2.2 / О1.4 Web: швидка перевірка повторного надсилання з тим самим Idempotency-Key (для вже завершених і не прострочених ключів)
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await _enrollmentRepo.GetIdempotencyRecordByKeyAsync(idempotencyKey, conn, null, cancellationToken);
            if (existing != null)
            {
                bool isExpired = existing.IdempotencyExpiresAt.HasValue
                    && existing.IdempotencyExpiresAt.Value <= DateTimeOffset.UtcNow;

                if (!isExpired)
                {
                    if (string.Equals(existing.State, "processing", StringComparison.OrdinalIgnoreCase))
                    {
                        return ServiceResult<WebEnrollmentResponseDto>.Failure(
                            StatusCodes.Status409Conflict,
                            "idempotency_key_in_progress",
                            "Операція за цим ключем ідемпотентності вже виконується",
                            "Запит із вказаним Idempotency-Key наразі перебуває в стані обробки (processing). Повторіть спробу пізніше.");
                    }

                    if (!EnrollmentDomainRules.IsMatchingWebEnrollmentFingerprint(
                            existing.RequestPayloadHash,
                            existing.StudentId,
                            existing.Operation,
                            studentId,
                            groupId))
                    {
                        return ServiceResult<WebEnrollmentResponseDto>.Failure(
                            StatusCodes.Status422UnprocessableEntity,
                            "idempotency_key_payload_mismatch",
                            "Невідповідність корисного навантаження ключа ідемпотентності",
                            "Ключ Idempotency-Key вже використано для іншого запиту або іншого студента.");
                    }

                    return ServiceResult<WebEnrollmentResponseDto>.Ok(existing.GetSavedOrFallbackResponse());
                }
            }
        }

        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // О2.2: Транзакційна синхронізація за Idempotency-Key між примірниками app-a та app-b.
            // Якщо інша транзакція вже утримує блокування на цей ключ понад вікно очікування або запис у стані 'processing', повертаємо 409.
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                bool keyLockAcquired = await _enrollmentRepo.TryAcquireIdempotencyKeyLockAsync(
                    idempotencyKey,
                    conn,
                    tx,
                    maxWaitMs: 150,
                    cancellationToken: cancellationToken);

                if (!keyLockAcquired)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return ServiceResult<WebEnrollmentResponseDto>.Failure(
                        StatusCodes.Status409Conflict,
                        "idempotency_key_in_progress",
                        "Операція за цим ключем ідемпотентності вже виконується",
                        "Запит із вказаним Idempotency-Key наразі обробляється паралельною транзакцією. Повторіть спробу пізніше.");
                }

                var existingInTx = await _enrollmentRepo.GetIdempotencyRecordByKeyAsync(idempotencyKey, conn, tx, cancellationToken);
                if (existingInTx != null)
                {
                    bool isExpiredInTx = existingInTx.IdempotencyExpiresAt.HasValue
                        && existingInTx.IdempotencyExpiresAt.Value <= DateTimeOffset.UtcNow;

                    if (isExpiredInTx)
                    {
                        await _enrollmentRepo.ClearExpiredIdempotencyKeyOnEnrollmentAsync(
                            existingInTx.EnrollmentId,
                            conn,
                            tx,
                            cancellationToken);
                    }
                    else
                    {
                        await tx.RollbackAsync(cancellationToken);

                        if (string.Equals(existingInTx.State, "processing", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                                StatusCodes.Status409Conflict,
                                "idempotency_key_in_progress",
                                "Операція за цим ключем ідемпотентності вже виконується",
                                "Запит із вказаним Idempotency-Key наразі перебуває в стані обробки (processing). Повторіть спробу пізніше.");
                        }

                        if (!EnrollmentDomainRules.IsMatchingWebEnrollmentFingerprint(
                                existingInTx.RequestPayloadHash,
                                existingInTx.StudentId,
                                existingInTx.Operation,
                                studentId,
                                groupId))
                        {
                            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                                StatusCodes.Status422UnprocessableEntity,
                                "idempotency_key_payload_mismatch",
                                "Невідповідність корисного навантаження ключа ідемпотентності",
                                "Ключ Idempotency-Key вже використано для іншого запиту або іншого студента.");
                        }

                        return ServiceResult<WebEnrollmentResponseDto>.Ok(existingInTx.GetSavedOrFallbackResponse());
                    }
                }
            }

            var student = await _enrollmentRepo.GetStudentForUpdateAsync(studentId, conn, tx, cancellationToken);
            if (student == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "student_not_found",
                    "Студента не знайдено",
                    $"Студент з ідентифікатором '{studentId}' відсутній у базі даних.");
            }

            var groupWithDisc = await _groupRepo.GetGroupWithDisciplineForUpdateAsync(groupId, conn, tx, cancellationToken);
            if (!groupWithDisc.HasValue)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "group_not_found",
                    "Групу дисципліни не знайдено",
                    $"Група з ідентифікатором '{groupId}' не існує.");
            }

            var group = groupWithDisc.Value.Group;
            string disciplineId = group.DisciplineId;
            string disciplineTitle = groupWithDisc.Value.DisciplineTitle;
            decimal disciplineEcts = groupWithDisc.Value.DisciplineEcts;

            bool alreadyEnrolled = await _enrollmentRepo.HasActiveEnrollmentForDisciplineAsync(studentId, disciplineId, conn, tx, cancellationToken);

            // Фіксуємо точний час безпосередньо перед перевіркою правил після отримання блокувань студента і групи
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            var policy = EnrollmentPolicy.Check(student, group, disciplineEcts, nowUtc, alreadyEnrolled);
            if (!policy.Allowed)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    policy.GetHttpStatusCode(),
                    policy.ErrorCode ?? "policy_violation",
                    policy.GetErrorTitle(),
                    policy.RejectionReason ?? "Запит відхилено правилами предметної області.");
            }

            var enrollmentId = Guid.NewGuid();
            var expiresAtUtc = nowUtc.AddMinutes(15);

            // П. 2 (О2.1): Атомарне умовне зменшення available у межах транзакції PostgreSQL
            bool decremented = await _groupRepo.DecrementAvailableAsync(groupId, conn, tx, cancellationToken);
            if (!decremented)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status409Conflict,
                    "group_full",
                    "У групі немає вільних місць",
                    $"У навчальній групі '{groupId}' вичерпано доступні місця.");
            }

            await _enrollmentRepo.UpdateStudentStatsAsync(studentId, +1, disciplineEcts, conn, tx, cancellationToken);

            var responseDto = new WebEnrollmentResponseDto(
                enrollmentId.ToString("D"),
                studentId,
                groupId,
                disciplineId,
                disciplineTitle,
                disciplineEcts,
                "held",
                1,
                nowUtc,
                expiresAtUtc,
                EnrollmentDomainRules.FormatInUniversityTimeZone(nowUtc, group.UniversityTz),
                EnrollmentDomainRules.FormatInUniversityTimeZone(expiresAtUtc, group.UniversityTz),
                group.UniversityTz,
                _options.InstanceId);

            string? savedResponseJson = !string.IsNullOrWhiteSpace(idempotencyKey)
                ? JsonSerializer.Serialize(responseDto)
                : null;
            string? savedLocation = !string.IsNullOrWhiteSpace(idempotencyKey)
                ? $"/v1/enrollments/{enrollmentId:D}"
                : null;
            DateTimeOffset? idempotencyExpiresAt = !string.IsNullOrWhiteSpace(idempotencyKey)
                ? nowUtc.AddMinutes(Math.Max(1, _options.IdempotencyKeyTtlMinutes))
                : null;

            await _enrollmentRepo.CreateEnrollmentAsync(
                enrollmentId,
                studentId,
                groupId,
                disciplineId,
                "held",
                1,
                nowUtc,
                expiresAtUtc,
                idempotencyKey,
                !string.IsNullOrWhiteSpace(idempotencyKey) ? payloadFingerprint : null,
                conn,
                tx,
                cancellationToken,
                EnrollmentDomainRules.CreateEnrollmentOperation,
                "completed",
                StatusCodes.Status201Created,
                savedResponseJson,
                savedLocation,
                idempotencyExpiresAt);

            await tx.CommitAsync(cancellationToken);

            // О2.3: Контрольована відмова після COMMIT, але до відправлення HTTP-відповіді клієнту
            FaultInjector.MaybeTriggerAfterCommitBeforeResponse(_options, enrollmentId, studentId, idempotencyKey);

            return ServiceResult<WebEnrollmentResponseDto>.Ok(responseDto);
        }
        catch (PostgresException pex) when (pex.SqlState == "23505" && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await tx.RollbackAsync(cancellationToken);
            var existing = await _enrollmentRepo.GetIdempotencyRecordByKeyAsync(idempotencyKey, conn, null, cancellationToken);
            if (existing != null)
            {
                if (string.Equals(existing.State, "processing", StringComparison.OrdinalIgnoreCase))
                {
                    return ServiceResult<WebEnrollmentResponseDto>.Failure(
                        StatusCodes.Status409Conflict,
                        "idempotency_key_in_progress",
                        "Операція за цим ключем ідемпотентності вже виконується",
                        "Запит із вказаним Idempotency-Key наразі перебуває в стані обробки (processing). Повторіть спробу пізніше.");
                }

                if (!EnrollmentDomainRules.IsMatchingWebEnrollmentFingerprint(
                        existing.RequestPayloadHash,
                        existing.StudentId,
                        existing.Operation,
                        studentId,
                        groupId))
                {
                    return ServiceResult<WebEnrollmentResponseDto>.Failure(
                        StatusCodes.Status422UnprocessableEntity,
                        "idempotency_key_payload_mismatch",
                        "Невідповідність корисного навантаження ключа ідемпотентності",
                        "Ключ Idempotency-Key вже використано для іншого запиту або іншого студента.");
                }

                return ServiceResult<WebEnrollmentResponseDto>.Ok(existing.GetSavedOrFallbackResponse());
            }
            throw;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ServiceResult<WebEnrollmentResponseDto>> ConfirmEnrollmentAsync(
        string studentId,
        Guid enrollmentId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Канонічний порядок блокувань: student -> discipline_group -> enrollment
            var student = await _enrollmentRepo.GetStudentForUpdateAsync(studentId, conn, tx, cancellationToken);
            if (student == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "student_not_found",
                    "Студента не знайдено",
                    $"Студент '{studentId}' відсутній у базі даних.");
            }

            var basicInfo = await _enrollmentRepo.GetEnrollmentBasicInfoAsync(enrollmentId, studentId, conn, tx, cancellationToken);
            if (basicInfo == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "enrollment_not_found",
                    "Запис не знайдено",
                    $"Запис із номером '{enrollmentId}' для студента '{studentId}' не знайдено.");
            }

            var group = await _groupRepo.GetGroupForUpdateAsync(basicInfo.Value.GroupId, conn, tx, cancellationToken);
            if (group == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "group_not_found",
                    "Групу не знайдено",
                    $"Групу '{basicInfo.Value.GroupId}' не знайдено.");
            }

            var enrollment = await _enrollmentRepo.GetEnrollmentForUpdateAsync(enrollmentId, studentId, conn, tx, cancellationToken);
            if (enrollment == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "enrollment_not_found",
                    "Запис не знайдено",
                    $"Запис із номером '{enrollmentId}' для студента '{studentId}' не знайдено.");
            }

            if (enrollment.Status == "confirmed")
            {
                await tx.CommitAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Ok(enrollment);
            }

            if (enrollment.Status == "cancelled")
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status409Conflict,
                    "enrollment_cancelled",
                    "Запис скасовано",
                    "Неможливо підтвердити скасований запис.");
            }

            // Перевіряємо наявність запису у decanate_roster за enrollment_id.
            // Якщо запис уже зафіксовано у відомості контуром РПО (DPC),
            // переводимо held у confirmed незалежно від expires_at,
            // не змінюючи available_seats і статистику студента (вони вже були виділені при створенні held).
            await using var rosterCheckCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM decanate_roster WHERE enrollment_id = @eid;", conn, tx);
            rosterCheckCmd.Parameters.AddWithValue("eid", enrollmentId);
            long inRosterCount = (long)(await rosterCheckCmd.ExecuteScalarAsync(cancellationToken) ?? 0L);

            if (inRosterCount > 0)
            {
                if (enrollment.Status == "held")
                {
                    await _enrollmentRepo.ConfirmEnrollmentHeldAsync(enrollmentId, conn, tx, cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);

                var updatedFromRoster = await _enrollmentRepo.GetEnrollmentByIdAsync(enrollmentId, studentId, cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Ok(updatedFromRoster ?? enrollment with { Status = "confirmed", Version = enrollment.Version + 1 });
            }

            if (enrollment.Status == "expired" || enrollment.ExpiresAt < DateTimeOffset.UtcNow)
            {
                // Атомарне звільнення простроченого запису без взаємоблокувань
                if (enrollment.Status != "expired")
                {
                    await using var expCmd = new NpgsqlCommand(
                        "UPDATE enrollment SET status = 'expired', version = version + 1 WHERE id = @id;", conn, tx);
                    expCmd.Parameters.AddWithValue("id", enrollmentId);
                    await expCmd.ExecuteNonQueryAsync(cancellationToken);

                    await _groupRepo.IncrementAvailableAsync(enrollment.GroupId, conn, tx, cancellationToken);
                    await _enrollmentRepo.UpdateStudentStatsAsync(studentId, -1, -enrollment.Ects, conn, tx, cancellationToken);
                }
                await tx.CommitAsync(cancellationToken);

                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status409Conflict,
                    "enrollment_expired",
                    "Термін резервування сплив",
                    "Час утримання місця (held, 15 хв) завершився, запис перейшов у стан 'expired'.");
            }

            bool confirmed = await _enrollmentRepo.ConfirmEnrollmentHeldAsync(enrollmentId, conn, tx, cancellationToken);
            if (!confirmed)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status409Conflict,
                    "enrollment_not_held",
                    "Неможливо підтвердити запис",
                    "Запис не перебуває у стані 'held' або вже був змінений.");
            }

            await tx.CommitAsync(cancellationToken);

            var updated = await _enrollmentRepo.GetEnrollmentByIdAsync(enrollmentId, studentId, cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Ok(updated!);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ServiceResult<WebEnrollmentResponseDto>> CancelEnrollmentAsync(
        string studentId,
        Guid enrollmentId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Канонічний порядок блокувань: student -> discipline_group -> enrollment
            var student = await _enrollmentRepo.GetStudentForUpdateAsync(studentId, conn, tx, cancellationToken);
            if (student == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "student_not_found",
                    "Студента не знайдено",
                    $"Студент '{studentId}' відсутній у базі даних.");
            }

            var basicInfo = await _enrollmentRepo.GetEnrollmentBasicInfoAsync(enrollmentId, studentId, conn, tx, cancellationToken);
            if (basicInfo == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "enrollment_not_found",
                    "Запис не знайдено",
                    $"Запис із номером '{enrollmentId}' для студента '{studentId}' не існує.");
            }

            var group = await _groupRepo.GetGroupForUpdateAsync(basicInfo.Value.GroupId, conn, tx, cancellationToken);
            if (group == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "group_not_found",
                    "Групу не знайдено",
                    $"Групу '{basicInfo.Value.GroupId}' не знайдено.");
            }

            var enrollment = await _enrollmentRepo.GetEnrollmentForUpdateAsync(enrollmentId, studentId, conn, tx, cancellationToken);
            if (enrollment == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status404NotFound,
                    "enrollment_not_found",
                    "Запис не знайдено",
                    $"Запис із номером '{enrollmentId}' для студента '{studentId}' не існує.");
            }

            if (enrollment.Status == "confirmed")
            {
                await tx.RollbackAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status409Conflict,
                    "enrollment_already_confirmed",
                    "Неможливо скасувати підтверджений запис",
                    "Підтверджений запис (confirmed) вже внесено до відомості деканату і він не підлягає скасуванню через студентський інтерфейс.");
            }

            // Перевіряємо наявність запису у decanate_roster за enrollment_id.
            // Якщо запис уже зафіксовано у відомості контуром РПО (DPC), скасовувати його
            // та повертати місце у групу неприпустимо — переводимо у confirmed та повертаємо 409 Conflict.
            await using var rosterCheckCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM decanate_roster WHERE enrollment_id = @eid;", conn, tx);
            rosterCheckCmd.Parameters.AddWithValue("eid", enrollmentId);
            long inRosterCount = (long)(await rosterCheckCmd.ExecuteScalarAsync(cancellationToken) ?? 0L);

            if (inRosterCount > 0)
            {
                if (enrollment.Status == "held")
                {
                    int confirmedVersion = enrollment.Version + 1;
                    await using var confirmCmd = new NpgsqlCommand(@"
                        UPDATE enrollment
                        SET status = 'confirmed',
                            version = @version
                        WHERE id = @id AND status = 'held';", conn, tx);
                    confirmCmd.Parameters.AddWithValue("id", enrollmentId);
                    confirmCmd.Parameters.AddWithValue("version", confirmedVersion);
                    await confirmCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);

                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status409Conflict,
                    "enrollment_already_confirmed",
                    "Неможливо скасувати підтверджений запис",
                    "Підтверджений запис (confirmed) вже внесено до відомості деканату і він не підлягає скасуванню через студентський інтерфейс.");
            }

            if (enrollment.Status == "cancelled")
            {
                await tx.CommitAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Ok(enrollment);
            }

            if (enrollment.Status == "expired" || enrollment.ExpiresAt < nowUtc)
            {
                if (enrollment.Status != "expired")
                {
                    await using var expCmd = new NpgsqlCommand(
                        "UPDATE enrollment SET status = 'expired', version = version + 1 WHERE id = @id;", conn, tx);
                    expCmd.Parameters.AddWithValue("id", enrollmentId);
                    await expCmd.ExecuteNonQueryAsync(cancellationToken);

                    await _groupRepo.IncrementAvailableAsync(enrollment.GroupId, conn, tx, cancellationToken);
                    await _enrollmentRepo.UpdateStudentStatsAsync(studentId, -1, -enrollment.Ects, conn, tx, cancellationToken);
                }
                await tx.CommitAsync(cancellationToken);

                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status409Conflict,
                    "enrollment_expired",
                    "Термін резервування сплив",
                    "Час утримання місця (held) завершився, запис перейшов у стан 'expired' і не підлягає скасуванню.");
            }

            int newVersion = enrollment.Version + 1;
            await _groupRepo.IncrementAvailableAsync(enrollment.GroupId, conn, tx, cancellationToken);
            await _enrollmentRepo.UpdateStudentStatsAsync(studentId, -1, -enrollment.Ects, conn, tx, cancellationToken);
            await _enrollmentRepo.CancelEnrollmentAsync(enrollmentId, newVersion, nowUtc, conn, tx, cancellationToken);

            await tx.CommitAsync(cancellationToken);

            var updated = await _enrollmentRepo.GetEnrollmentByIdAsync(enrollmentId, studentId, cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Ok(updated!);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ServiceResult<WebEnrollmentListResponseDto>> GetStudentEnrollmentsAsync(
        string studentId,
        int limit,
        string? cursorRaw,
        CancellationToken cancellationToken = default)
    {
        var student = await _enrollmentRepo.GetStudentByIdAsync(studentId, cancellationToken);
        if (student == null)
        {
            return ServiceResult<WebEnrollmentListResponseDto>.Failure(
                StatusCodes.Status404NotFound,
                "student_not_found",
                "Студента не знайдено",
                $"Студент '{studentId}' відсутній у базі даних.");
        }

        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = !string.IsNullOrWhiteSpace(cursorRaw);

        if (hasCursor && !EnrollmentDomainRules.TryDecodeCursor(cursorRaw, out cursorCreatedAt, out cursorId))
        {
            return ServiceResult<WebEnrollmentListResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_cursor",
                "Некоректний курсор",
                "Переданий курсор пагінації не відповідає очікуваному непрозорому формату Base64.");
        }

        var items = await _enrollmentRepo.GetStudentEnrollmentsCursorAsync(
            studentId, limit, hasCursor ? cursorCreatedAt : null, hasCursor ? cursorId : null, cancellationToken);

        string? nextCursor = null;
        var visibleItems = items.Take(limit).ToList();
        if (items.Count > limit)
        {
            var lastVisible = visibleItems.Last();
            nextCursor = EnrollmentDomainRules.EncodeCursor(lastVisible.CreatedAt, Guid.Parse(lastVisible.Id));
        }

        var responseDto = new WebEnrollmentListResponseDto(
            visibleItems,
            nextCursor,
            limit,
            student.Id,
            student.ActiveCount,
            student.MaxDisciplines,
            student.ActiveEcts,
            EnrollmentDomainRules.MaxSemesterEcts);

        return ServiceResult<WebEnrollmentListResponseDto>.Ok(responseDto);
    }

    public async Task<ServiceResult<WebEnrollmentResponseDto>> GetEnrollmentByIdAsync(
        string studentId,
        Guid enrollmentId,
        CancellationToken cancellationToken = default)
    {
        // RFC 9110: GET є безпечним (safe) та ідемпотентним методом і виконує виключно читання
        var item = await _enrollmentRepo.GetEnrollmentByIdAsync(enrollmentId, studentId, cancellationToken);
        if (item == null)
        {
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status404NotFound,
                "enrollment_not_found",
                "Запис не знайдено",
                $"Запис '{enrollmentId}' для студента '{studentId}' не існує.");
        }

        return ServiceResult<WebEnrollmentResponseDto>.Ok(item);
    }

    public async Task<RosterConfirmationResult> ConfirmEnrollmentDetailedAsync(
        Guid enrollmentId,
        Guid requestId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        // 1. Першим кроком перевіряємо наявність запису у decanate_roster за точною зв'язкою (request_id, enrollment_id).
        // Якщо запис відсутній або прив'язаний до іншого request_id / enrollment_id — відхиляємо як permanent failure.
        await using var verifyCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM decanate_roster WHERE request_id = @reqId AND enrollment_id = @eid;", conn, tx);
        verifyCmd.Parameters.AddWithValue("reqId", requestId);
        verifyCmd.Parameters.AddWithValue("eid", enrollmentId);
        long rosterCount = (long)(await verifyCmd.ExecuteScalarAsync(cancellationToken) ?? 0L);

        if (rosterCount == 0)
        {
            return RosterConfirmationResult.PermanentFailure;
        }

        // 2. Блокуємо та зчитуємо поточний стан Web-запису
        await using var chkCmd = new NpgsqlCommand(
            "SELECT status, expires_at FROM enrollment WHERE id = @eid FOR UPDATE OF enrollment;", conn, tx);
        chkCmd.Parameters.AddWithValue("eid", enrollmentId);
        await using var reader = await chkCmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return RosterConfirmationResult.PermanentFailure;
        }

        string currentStatus = reader.GetString(0);
        DateTimeOffset expiresAt = reader.GetFieldValue<DateTimeOffset>(1);
        await reader.CloseAsync();

        // 3. Ідемпотентний повтор для вже підтвердженого статусу (confirmed): операція вважається успішною
        if (currentStatus == "confirmed")
        {
            return RosterConfirmationResult.AlreadyConfirmed;
        }

        // 4. Постійні порушення: запис скасовано ('cancelled') або переведено в інший термінальний стан.
        // Зауваження: оскільки запис успішно внесено до відомості decanate_roster у межах чинного вікна резервування,
        // затримка сповіщення Web або відпрацювання reconciliation після закінчення expires_at НЕ є перешкодою
        // для фіксації підтвердження (held -> confirmed), адже офіційне зарахування студента вже зафіксовано.
        if (currentStatus != "held")
        {
            return RosterConfirmationResult.PermanentFailure;
        }

        // 5. Оновлюємо статус із 'held' на 'confirmed' з атомарним інкрементом version
        await using var updateCmd = new NpgsqlCommand(@"
            UPDATE enrollment
            SET status = 'confirmed',
                version = version + 1
            WHERE id = @eid AND status = 'held';", conn, tx);
        updateCmd.Parameters.AddWithValue("eid", enrollmentId);
        int rows = await updateCmd.ExecuteNonQueryAsync(cancellationToken);

        return rows > 0 ? RosterConfirmationResult.Success : RosterConfirmationResult.TransientFailure;
    }

    public async Task<RosterConfirmationResult> ConfirmEnrollmentDetailedAsync(
        Guid enrollmentId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            var result = await ConfirmEnrollmentDetailedAsync(enrollmentId, requestId, conn, tx, cancellationToken);
            if (result is RosterConfirmationResult.Success or RosterConfirmationResult.AlreadyConfirmed)
            {
                await tx.CommitAsync(cancellationToken);
            }
            else
            {
                await tx.RollbackAsync(cancellationToken);
            }
            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> ConfirmEnrollmentFromRosterAsync(
        Guid enrollmentId,
        Guid requestId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken cancellationToken = default)
    {
        var result = await ConfirmEnrollmentDetailedAsync(enrollmentId, requestId, conn, tx, cancellationToken);
        return result is RosterConfirmationResult.Success or RosterConfirmationResult.AlreadyConfirmed;
    }

    public async Task<bool> ConfirmEnrollmentFromRosterAsync(
        Guid enrollmentId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var result = await ConfirmEnrollmentDetailedAsync(enrollmentId, requestId, cancellationToken);
        return result is RosterConfirmationResult.Success or RosterConfirmationResult.AlreadyConfirmed;
    }

    public async Task<int> ReconcilePendingRosterConfirmationsAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = await _enrollmentRepo.GetPendingRosterConfirmationsAsync(cancellationToken);
        if (pending.Count == 0) return 0;

        int reconciled = 0;
        foreach (var (enrollmentId, requestId) in pending)
        {
            try
            {
                if (await ConfirmEnrollmentFromRosterAsync(enrollmentId, requestId, cancellationToken))
                {
                    reconciled++;
                }
            }
            catch (Exception ex)
            {
                StructuredJsonLogger.LogEvent(_options.InstanceId, "roster_reconciliation_error", new Dictionary<string, object?>
                {
                    ["enrollment_id"] = enrollmentId,
                    ["request_id"] = requestId,
                    ["error"] = ex.Message
                });
            }
        }
        return reconciled;
    }

    public async Task<ServiceResult<WebDisciplineListResponseDto>> GetDisciplinesAsync(
        int limit,
        string? cursorRaw,
        bool includeTest,
        CancellationToken cancellationToken = default)
    {
        string? decodedAfterId = null;
        if (!string.IsNullOrWhiteSpace(cursorRaw))
        {
            string decodedString;
            try
            {
                string normalized = cursorRaw.Trim().Replace('-', '+').Replace('_', '/');
                switch (normalized.Length % 4)
                {
                    case 2: normalized += "=="; break;
                    case 3: normalized += "="; break;
                }
                decodedString = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            }
            catch (FormatException)
            {
                return ServiceResult<WebDisciplineListResponseDto>.Failure(
                    StatusCodes.Status400BadRequest,
                    "invalid_cursor",
                    "Некоректний курсор",
                    "Переданий курсор не є валідним рядком Base64.");
            }

            const string prefix = "c10disc|";
            if (!decodedString.StartsWith(prefix, StringComparison.Ordinal))
            {
                return ServiceResult<WebDisciplineListResponseDto>.Failure(
                    StatusCodes.Status400BadRequest,
                    "invalid_cursor",
                    "Некоректний курсор",
                    "Переданий курсор має некоректний префікс версії або недійсний формат.");
            }

            decodedAfterId = decodedString.Substring(prefix.Length).Trim();
            if (string.IsNullOrWhiteSpace(decodedAfterId) || !await _groupRepo.DisciplineExistsAsync(decodedAfterId, cancellationToken))
            {
                return ServiceResult<WebDisciplineListResponseDto>.Failure(
                    StatusCodes.Status400BadRequest,
                    "invalid_cursor",
                    "Некоректний курсор",
                    $"Курсор посилається на невідомий або недійсний ідентифікатор дисципліни '{decodedAfterId}'.");
            }
        }

        var rawDisciplines = (await _groupRepo.GetDisciplinesAsync(limit, decodedAfterId, includeTest, cancellationToken)).ToList();

        string? nextCursor = null;
        if (rawDisciplines.Count > limit)
        {
            var lastItem = rawDisciplines[limit - 1];
            string rawCursor = $"c10disc|{lastItem.Id}";
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCursor))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            rawDisciplines.RemoveAt(rawDisciplines.Count - 1);
        }

        var discIds = rawDisciplines.Select(d => d.Id).ToList();
        var relevantGroups = await _groupRepo.GetGroupsByDisciplineIdsAsync(discIds, cancellationToken);

        var nowUtc = DateTimeOffset.UtcNow;
        var groupsByDiscipline = relevantGroups
            .GroupBy(g => g.DisciplineId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var disciplineDtos = new List<WebDisciplineDto>();
        foreach (var d in rawDisciplines)
        {
            var grpDtos = new List<WebDisciplineGroupDto>();
            if (groupsByDiscipline.TryGetValue(d.Id, out var grpEntities))
            {
                foreach (var g in grpEntities)
                {
                    bool windowActive = !g.Closed && EnrollmentDomainRules.IsWithinEnrollmentWindow(nowUtc, g.OpensAt, g.ClosesAt);
                    grpDtos.Add(new WebDisciplineGroupDto(
                        g.Id,
                        g.DisciplineId,
                        g.GroupCode,
                        g.Seats,
                        g.Available,
                        g.OpensAt,
                        g.ClosesAt,
                        EnrollmentDomainRules.FormatInUniversityTimeZone(g.OpensAt, g.UniversityTz),
                        EnrollmentDomainRules.FormatInUniversityTimeZone(g.ClosesAt, g.UniversityTz),
                        g.UniversityTz,
                        g.Closed,
                        windowActive));
                }
            }

            disciplineDtos.Add(new WebDisciplineDto(
                d.Id,
                d.Code,
                d.Title,
                d.Ects,
                grpDtos));
        }

        return ServiceResult<WebDisciplineListResponseDto>.Ok(new WebDisciplineListResponseDto(disciplineDtos, nextCursor));
    }

    // О2.2: Пакетне очищення прострочених ключів ідемпотентності невеликими порціями (FOR UPDATE SKIP LOCKED)
    public Task<int> CleanupExpiredIdempotencyKeysAsync(
        int? batchSize = null,
        CancellationToken cancellationToken = default)
    {
        int effectiveBatch = batchSize.HasValue && batchSize.Value > 0
            ? batchSize.Value
            : Math.Max(1, _options.IdempotencyCleanupBatchSize);
        return _enrollmentRepo.CleanupExpiredIdempotencyKeysBatchAsync(effectiveBatch, cancellationToken);
    }
}
