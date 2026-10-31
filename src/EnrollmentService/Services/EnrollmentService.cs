using System.Globalization;
using EnrollmentService.Domain;
using EnrollmentService.Repositories;

namespace EnrollmentService.Services;

public sealed class EnrollmentService : IEnrollmentService
{
    private readonly IGroupRepository _groupRepo;
    private readonly IEnrollmentRepository _enrollmentRepo;
    private readonly IDecanateRepository _decanateRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ServiceOptions _options;

    public EnrollmentService(
        IGroupRepository groupRepo,
        IEnrollmentRepository enrollmentRepo,
        IDecanateRepository decanateRepo,
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
        string? simulatedNowHeader,
        CancellationToken cancellationToken = default)
    {
        // 1. Валідація ідентифікатора групи
        if (string.IsNullOrWhiteSpace(dto.GroupId) || dto.GroupId.Trim().Length > 64 || dto.GroupId.Contains(' '))
        {
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_group_id_format",
                "Некоректні дані запиту (Unprocessable Entity)",
                "Поле 'group_id' є обов'язковим, не може бути порожнім або містити пробіли.");
        }

        string groupId = dto.GroupId.Trim();

        // 2. Симуляція поточного часу (evaluated_at / X-Simulated-Now) дозволена ВИКЛЮЧНО при ALLOW_TEST_OVERRIDES=true
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        bool hasBodyEval = !string.IsNullOrWhiteSpace(dto.EvaluatedAt);
        bool hasHeaderSim = !string.IsNullOrWhiteSpace(simulatedNowHeader);

        if (hasBodyEval || hasHeaderSim)
        {
            if (!_options.AllowTestOverrides)
            {
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status403Forbidden,
                    "test_override_disabled",
                    "Тестове перевизначення часу заборонено",
                    "Передача evaluated_at або заголовка X-Simulated-Now вимкнена у штатному режимі (ALLOW_TEST_OVERRIDES=false).");
            }

            string simulatedTimeRaw = hasBodyEval ? dto.EvaluatedAt!.Trim() : simulatedNowHeader!.Trim();
            if (!DateTimeOffset.TryParse(simulatedTimeRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out nowUtc))
            {
                return ServiceResult<WebEnrollmentResponseDto>.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    "invalid_evaluated_at",
                    "Некоректний формат часу evaluated_at",
                    "Поле evaluated_at має бути у форматі ISO 8601 (наприклад, 2026-09-01T07:00:00Z).");
            }
        }

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // 3. Блокування рядка студента (FOR UPDATE — крок 1 канонічного порядку блокувань)
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

        // 4. Блокування рядка групи дисципліни (FOR UPDATE — крок 2 канонічного порядку блокувань)
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

        // 5. Перевірка інваріантів предметної області (О1.1, О1.4, Д1.1, Д1.5)
        if (group.Closed)
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status409Conflict,
                "group_closed",
                "Запис у групу закрито",
                $"Навчальна група '{groupId}' закрита працівником деканату (closed = true).");
        }

        if (!EnrollmentDomainRules.IsWithinEnrollmentWindow(nowUtc, group.OpensAt, group.ClosesAt))
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status409Conflict,
                "outside_enrollment_window",
                "Поза вікном запису на дисципліну",
                $"Момент запиту ({EnrollmentDomainRules.FormatInUniversityTimeZone(nowUtc, group.UniversityTz)}) не входить у напіввідкрите вікно [{EnrollmentDomainRules.FormatInUniversityTimeZone(group.OpensAt, group.UniversityTz)}, {EnrollmentDomainRules.FormatInUniversityTimeZone(group.ClosesAt, group.UniversityTz)}).",
                new Dictionary<string, object?>
                {
                    ["evaluated_at_utc"] = nowUtc,
                    ["opens_at_utc"] = group.OpensAt,
                    ["closes_at_utc"] = group.ClosesAt,
                    ["opens_at_local"] = EnrollmentDomainRules.FormatInUniversityTimeZone(group.OpensAt, group.UniversityTz),
                    ["closes_at_local"] = EnrollmentDomainRules.FormatInUniversityTimeZone(group.ClosesAt, group.UniversityTz)
                });
        }

        bool alreadyEnrolled = await _enrollmentRepo.HasActiveEnrollmentForDisciplineAsync(
            studentId, disciplineId, conn, tx, cancellationToken);
        if (alreadyEnrolled)
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status409Conflict,
                "already_enrolled_in_discipline",
                "Повторний запис на дисципліну",
                $"Студент '{studentId}' вже має активний запис на дисципліну '{disciplineId}'.");
        }

        if (group.Available <= 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status409Conflict,
                "no_available_seats",
                "Немає вільних місць у групі",
                $"У групі '{groupId}' вичерпано всі {group.Seats} місць (available = 0).");
        }

        if (!EnrollmentDomainRules.CanEnrollWithDisciplineLimit(student.ActiveCount, student.MaxDisciplines))
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status409Conflict,
                "max_disciplines_exceeded",
                "Перевищено ліміт активних дисциплін",
                $"Студент '{studentId}' вже має {student.ActiveCount} із {student.MaxDisciplines} дозволених вибіркових дисциплін.");
        }

        if (!EnrollmentDomainRules.CanEnrollWithEcts(student.ActiveEcts, disciplineEcts))
        {
            decimal projectedEcts = student.ActiveEcts + disciplineEcts;
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status409Conflict,
                "ects_limit_exceeded",
                "Перевищено семестровий ліміт кредитів ЄКТС",
                $"Сума кредитів після запису становить {projectedEcts.ToString("0.0", CultureInfo.InvariantCulture)} ЄКТС, що перевищує максимум {EnrollmentDomainRules.MaxSemesterEcts.ToString("0.0", CultureInfo.InvariantCulture)} ЄКТС.",
                new Dictionary<string, object?>
                {
                    ["current_ects"] = student.ActiveEcts,
                    ["discipline_ects"] = disciplineEcts,
                    ["projected_ects"] = projectedEcts,
                    ["max_ects"] = EnrollmentDomainRules.MaxSemesterEcts
                });
        }

        // 6. Атомарне оновлення єдиного спільного ресурсу discipline_group, студента та вставка enrollment
        bool decremented = await _groupRepo.DecrementAvailableAsync(groupId, conn, tx, cancellationToken);
        if (!decremented)
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status409Conflict,
                "no_available_seats",
                "Немає вільних місць у групі",
                $"У групі '{groupId}' не залишилося вільних місць.");
        }

        await _enrollmentRepo.UpdateStudentStatsAsync(studentId, +1, disciplineEcts, conn, tx, cancellationToken);

        Guid enrollmentId = Guid.NewGuid();
        DateTimeOffset createdAtUtc = (hasBodyEval || hasHeaderSim) ? nowUtc : DateTimeOffset.UtcNow;
        DateTimeOffset expiresAtUtc = createdAtUtc.AddMinutes(15);

        await _enrollmentRepo.CreateEnrollmentAsync(
            enrollmentId,
            studentId,
            groupId,
            disciplineId,
            "held",
            1,
            createdAtUtc,
            expiresAtUtc,
            conn,
            tx,
            cancellationToken);

        // О1.4: Фіксуємо транзакцію ПЕРЕД створенням успішної відповіді 201 Created
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
            createdAtUtc,
            expiresAtUtc,
            EnrollmentDomainRules.FormatInUniversityTimeZone(createdAtUtc, group.UniversityTz),
            EnrollmentDomainRules.FormatInUniversityTimeZone(expiresAtUtc, group.UniversityTz),
            group.UniversityTz,
            _options.InstanceId);

        return ServiceResult<WebEnrollmentResponseDto>.Ok(responseDto, StatusCodes.Status201Created);
    }

    public async Task<ServiceResult<WebEnrollmentListResponseDto>> GetEnrollmentsAsync(
        string studentId,
        int limit,
        string? cursorRaw,
        CancellationToken cancellationToken = default)
    {
        bool hasCursor = !string.IsNullOrWhiteSpace(cursorRaw);
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = Guid.Empty;

        if (hasCursor && !EnrollmentDomainRules.TryDecodeCursor(cursorRaw, out cursorCreatedAt, out cursorId))
        {
            return ServiceResult<WebEnrollmentListResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_cursor",
                "Пошкоджений або некоректний курсор пагінації",
                $"Значення курсора '{cursorRaw}' не вдалося розпізнати. Очікується непрозорий курсор next_cursor із попередньої сторінки.");
        }

        var student = await _enrollmentRepo.GetStudentByIdAsync(studentId, cancellationToken);
        int activeCount = student?.ActiveCount ?? 0;
        int maxDisciplines = student?.MaxDisciplines ?? 5;
        decimal activeEcts = student?.ActiveEcts ?? 0.0m;

        var items = (await _enrollmentRepo.GetStudentEnrollmentsCursorAsync(
            studentId,
            limit,
            hasCursor ? cursorCreatedAt : null,
            hasCursor ? cursorId : null,
            cancellationToken)).ToList();

        string? nextCursor = null;
        if (items.Count > limit)
        {
            var lastVisible = items[limit - 1];
            nextCursor = EnrollmentDomainRules.EncodeCursor(lastVisible.CreatedAt, Guid.Parse(lastVisible.Id));
            items.RemoveAt(items.Count - 1);
        }

        var resultDto = new WebEnrollmentListResponseDto(
            items,
            nextCursor,
            limit,
            studentId,
            activeCount,
            maxDisciplines,
            activeEcts,
            EnrollmentDomainRules.MaxSemesterEcts);

        return ServiceResult<WebEnrollmentListResponseDto>.Ok(resultDto);
    }

    public async Task<ServiceResult<WebEnrollmentResponseDto>> GetEnrollmentByIdAsync(
        string studentId,
        string enrollmentIdRaw,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(enrollmentIdRaw, out Guid enrollmentId))
        {
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_enrollment_id",
                "Некоректний формат ідентифікатора запису",
                $"Ідентифікатор '{enrollmentIdRaw}' не є коректним UUID.");
        }

        var enrollment = await _enrollmentRepo.GetEnrollmentByIdAsync(enrollmentId, studentId, cancellationToken);
        if (enrollment == null)
        {
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status404NotFound,
                "enrollment_not_found",
                "Запис на дисципліну не знайдено",
                $"Запис з ідентифікатором '{enrollmentIdRaw}' відсутній або не належить користувачу '{studentId}'.");
        }

        return ServiceResult<WebEnrollmentResponseDto>.Ok(enrollment);
    }

    public async Task<ServiceResult<WebEnrollmentResponseDto>> CancelEnrollmentAsync(
        string studentId,
        string enrollmentIdRaw,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(enrollmentIdRaw, out Guid enrollmentId))
        {
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_enrollment_id",
                "Некоректний формат ідентифікатора запису",
                $"Ідентифікатор '{enrollmentIdRaw}' не є коректним UUID.");
        }

        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // Попереднє читання ідентифікаторів без блокування для визначення порядку захоплення FOR UPDATE
        var lookup = await _enrollmentRepo.GetEnrollmentLookupAsync(enrollmentId, conn, tx, cancellationToken);
        if (!lookup.HasValue)
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status404NotFound,
                "enrollment_not_found",
                "Запис на дисципліну не знайдено",
                $"Запис з ідентифікатором '{enrollmentIdRaw}' відсутній у базі даних.");
        }

        string ownerStudentId = lookup.Value.StudentId;
        string targetGroupId = lookup.Value.GroupId;

        if (!string.Equals(ownerStudentId, studentId, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status404NotFound,
                "enrollment_not_found",
                "Запис на дисципліну не знайдено",
                $"Запис '{enrollmentIdRaw}' не належить користувачу '{studentId}'.");
        }

        // Крок 1 канонічного порядку блокувань: student FOR UPDATE
        await _enrollmentRepo.GetStudentForUpdateAsync(studentId, conn, tx, cancellationToken);

        // Крок 2 канонічного порядку блокувань: discipline_group FOR UPDATE
        await _groupRepo.GetGroupWithDisciplineForUpdateAsync(targetGroupId, conn, tx, cancellationToken);

        // Крок 3 канонічного порядку блокувань: enrollment FOR UPDATE
        var enrollment = await _enrollmentRepo.GetEnrollmentForUpdateAsync(enrollmentId, studentId, conn, tx, cancellationToken);
        if (enrollment == null)
        {
            await tx.RollbackAsync(cancellationToken);
            return ServiceResult<WebEnrollmentResponseDto>.Failure(
                StatusCodes.Status404NotFound,
                "enrollment_not_found",
                "Запис на дисципліну не знайдено",
                $"Запис з ідентифікатором '{enrollmentIdRaw}' відсутній у базі даних.");
        }

        int newVersion = enrollment.Version;
        if (enrollment.Status is "held" or "confirmed")
        {
            newVersion++;
            var now = DateTimeOffset.UtcNow;
            await _enrollmentRepo.CancelEnrollmentAsync(enrollmentId, newVersion, now, conn, tx, cancellationToken);
            await _groupRepo.IncrementAvailableAsync(targetGroupId, conn, tx, cancellationToken);
            await _enrollmentRepo.UpdateStudentStatsAsync(studentId, -1, -enrollment.Ects, conn, tx, cancellationToken);
            await _decanateRepo.DeleteRosterEntryForEnrollmentAsync(enrollmentId, studentId, targetGroupId, conn, tx, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        var resultDto = enrollment with
        {
            Status = "cancelled",
            Version = newVersion
        };

        return ServiceResult<WebEnrollmentResponseDto>.Ok(resultDto);
    }

    public async Task<ServiceResult<WebDisciplineListResponseDto>> GetDisciplinesAsync(
        int limit,
        string? afterId,
        bool includeTest,
        CancellationToken cancellationToken = default)
    {
        var rawDisciplines = (await _groupRepo.GetDisciplinesAsync(limit, afterId, includeTest, cancellationToken)).ToList();

        string? nextCursor = null;
        if (rawDisciplines.Count > limit)
        {
            nextCursor = rawDisciplines[limit - 1].Id;
            rawDisciplines.RemoveAt(rawDisciplines.Count - 1);
        }

        var allGroups = await _groupRepo.GetAllDisciplineGroupsAsync(cancellationToken);
        var groupsByDiscipline = new Dictionary<string, List<WebDisciplineGroupDto>>(StringComparer.Ordinal);
        var nowUtc = DateTimeOffset.UtcNow;

        foreach (var grp in allGroups)
        {
            bool windowActive = !grp.Closed && EnrollmentDomainRules.IsWithinEnrollmentWindow(nowUtc, grp.OpensAt, grp.ClosesAt);
            var groupDto = new WebDisciplineGroupDto(
                grp.Id,
                grp.DisciplineId,
                grp.GroupCode,
                grp.Seats,
                grp.Available,
                grp.OpensAt,
                grp.ClosesAt,
                EnrollmentDomainRules.FormatInUniversityTimeZone(grp.OpensAt, grp.UniversityTz),
                EnrollmentDomainRules.FormatInUniversityTimeZone(grp.ClosesAt, grp.UniversityTz),
                grp.UniversityTz,
                grp.Closed,
                windowActive);

            if (!groupsByDiscipline.TryGetValue(grp.DisciplineId, out var list))
            {
                list = [];
                groupsByDiscipline[grp.DisciplineId] = list;
            }
            list.Add(groupDto);
        }

        var items = rawDisciplines.Select(d => new WebDisciplineDto(
            d.Id,
            d.Code,
            d.Title,
            d.Ects,
            groupsByDiscipline.TryGetValue(d.Id, out var gList) ? gList : []
        )).ToList();

        return ServiceResult<WebDisciplineListResponseDto>.Ok(new WebDisciplineListResponseDto(items, nextCursor));
    }

    public async Task<IReadOnlyList<WebStudentDto>> GetStudentsAsync(CancellationToken cancellationToken = default)
    {
        var students = await _enrollmentRepo.GetStudentsAsync(cancellationToken);
        return students.Select(s => new WebStudentDto(
            s.Id,
            s.Email,
            s.FullName,
            s.MaxDisciplines,
            s.ActiveCount,
            s.ActiveEcts,
            EnrollmentDomainRules.MaxSemesterEcts
        )).ToList();
    }
}
