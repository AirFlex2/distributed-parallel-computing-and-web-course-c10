using System.Globalization;
using System.Text;
using EnrollmentService.Domain;
using EnrollmentService.Repositories;
using Npgsql;

namespace EnrollmentService.Services;

public sealed class EnrollmentService
{
    private readonly GroupRepository _groupRepo;
    private readonly EnrollmentRepository _enrollmentRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ServiceOptions _options;

    public EnrollmentService(
        GroupRepository groupRepo,
        EnrollmentRepository enrollmentRepo,
        IDbConnectionFactory connectionFactory,
        ServiceOptions options)
    {
        _groupRepo = groupRepo;
        _enrollmentRepo = enrollmentRepo;
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
        string payloadFingerprint = $"{studentId}:{groupId}";

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        // О2.2 / О1.4 Web: перевірка повторного надсилання з тим самим Idempotency-Key
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await _enrollmentRepo.GetEnrollmentByIdempotencyKeyAsync(idempotencyKey, conn, null, cancellationToken);
            if (existing != null)
            {
                if (!string.Equals(existing.Value.RequestPayloadHash, payloadFingerprint, StringComparison.Ordinal) ||
                    !string.Equals(existing.Value.StudentId, studentId, StringComparison.Ordinal))
                {
                    return ServiceResult<WebEnrollmentResponseDto>.Failure(
                        StatusCodes.Status409Conflict,
                        "idempotency_key_conflict",
                        "Конфлікт ключа ідемпотентності",
                        "Ключ Idempotency-Key вже використано для іншого запиту або іншого студента.");
                }

                return ServiceResult<WebEnrollmentResponseDto>.Ok(existing.Value.Enrollment);
            }
        }

        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
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

            // Повторна перевірка ідемпотентності під транзакційним блокуванням (на випадок конкурентного надсилання)
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existingInTx = await _enrollmentRepo.GetEnrollmentByIdempotencyKeyAsync(idempotencyKey, conn, tx, cancellationToken);
                if (existingInTx != null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    if (!string.Equals(existingInTx.Value.RequestPayloadHash, payloadFingerprint, StringComparison.Ordinal) ||
                        !string.Equals(existingInTx.Value.StudentId, studentId, StringComparison.Ordinal))
                    {
                        return ServiceResult<WebEnrollmentResponseDto>.Failure(
                            StatusCodes.Status409Conflict,
                            "idempotency_key_conflict",
                            "Конфлікт ключа ідемпотентності",
                            "Ключ Idempotency-Key вже використано для іншого запиту або іншого студента.");
                    }

                    return ServiceResult<WebEnrollmentResponseDto>.Ok(existingInTx.Value.Enrollment);
                }
            }

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

            // П. 2: Перевірка результату декременту available
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
                payloadFingerprint,
                conn,
                tx,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);

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

            return ServiceResult<WebEnrollmentResponseDto>.Ok(responseDto);
        }
        catch (PostgresException pex) when (pex.SqlState == "23505" && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await tx.RollbackAsync(cancellationToken);
            var existing = await _enrollmentRepo.GetEnrollmentByIdempotencyKeyAsync(idempotencyKey, conn, null, cancellationToken);
            if (existing != null)
            {
                if (!string.Equals(existing.Value.RequestPayloadHash, payloadFingerprint, StringComparison.Ordinal) ||
                    !string.Equals(existing.Value.StudentId, studentId, StringComparison.Ordinal))
                {
                    return ServiceResult<WebEnrollmentResponseDto>.Failure(
                        StatusCodes.Status409Conflict,
                        "idempotency_key_conflict",
                        "Конфлікт ключа ідемпотентності",
                        "Ключ Idempotency-Key вже використано для іншого запиту або іншого студента.");
                }

                return ServiceResult<WebEnrollmentResponseDto>.Ok(existing.Value.Enrollment);
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

            if (enrollment.Status == "cancelled" || enrollment.Status == "expired")
            {
                await tx.CommitAsync(cancellationToken);
                return ServiceResult<WebEnrollmentResponseDto>.Ok(enrollment);
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
}
