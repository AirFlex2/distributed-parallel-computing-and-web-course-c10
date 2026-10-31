using EnrollmentService.Metrics;
using EnrollmentService.Repositories;

namespace EnrollmentService.Services.Processing;

public interface ISafeEnrollmentProcessor
{
    Task<(string Status, string? Reason)> ProcessSafeAsync(
        QueuedRequestItem item,
        CancellationToken cancellationToken = default);
}

public sealed class SafeEnrollmentProcessor : ISafeEnrollmentProcessor
{
    private readonly IGroupRepository _groupRepo;
    private readonly IEnrollmentRepository _enrollmentRepo;
    private readonly IEnrollmentRequestRepository _requestRepo;
    private readonly IDecanateRepository _decanateRepo;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly MetricsCollector _metrics;

    public SafeEnrollmentProcessor(
        IGroupRepository groupRepo,
        IEnrollmentRepository enrollmentRepo,
        IEnrollmentRequestRepository requestRepo,
        IDecanateRepository decanateRepo,
        IDbConnectionFactory connectionFactory,
        MetricsCollector metrics)
    {
        _groupRepo = groupRepo;
        _enrollmentRepo = enrollmentRepo;
        _requestRepo = requestRepo;
        _decanateRepo = decanateRepo;
        _connectionFactory = connectionFactory;
        _metrics = metrics;
    }

    public async Task<(string Status, string? Reason)> ProcessSafeAsync(
        QueuedRequestItem item,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        string finalStatus;
        string? rejectionReason = null;
        string? assignedGroupId = null;

        // 1. Крок 1 канонічного порядку блокувань: student FOR UPDATE
        var student = await _enrollmentRepo.GetStudentForUpdateAsync(item.StudentId, conn, tx, cancellationToken);
        bool studentExists = student != null;
        int maxDisciplines = student?.MaxDisciplines ?? 5;
        int studentActiveCount = student?.ActiveCount ?? 0;
        decimal studentActiveEcts = student?.ActiveEcts ?? 0.0m;

        // 2. Крок 2 канонічного порядку блокувань: discipline_group FOR UPDATE
        var groupWithDisc = await _groupRepo.GetGroupWithDisciplineForUpdateAsync(item.GroupId, conn, tx, cancellationToken);
        bool groupExists = groupWithDisc.HasValue;
        string disciplineId = groupWithDisc?.Group.DisciplineId ?? string.Empty;
        decimal disciplineEcts = groupWithDisc?.DisciplineEcts ?? 0.0m;
        int seats = groupWithDisc?.Group.Seats ?? 0;
        DateTimeOffset opensAt = groupWithDisc?.Group.OpensAt ?? default;
        DateTimeOffset closesAt = groupWithDisc?.Group.ClosesAt ?? default;
        bool closed = groupWithDisc?.Group.Closed ?? false;

        // Ідемпотентна перевірка за ключем request_id у відомості деканату (decanate_roster)
        string? existingRosterGroup = await _decanateRepo.GetRosterGroupForRequestAsync(item.Id, conn, tx, cancellationToken);

        var nowUtc = DateTimeOffset.UtcNow;

        if (existingRosterGroup != null)
        {
            finalStatus = "done";
            assignedGroupId = existingRosterGroup;
        }
        else if (!groupExists)
        {
            finalStatus = "failed";
            rejectionReason = $"Навчальну групу '{item.GroupId}' не знайдено.";
        }
        else if (closed)
        {
            finalStatus = "rejected";
            rejectionReason = $"Навчальна група '{item.GroupId}' закрита працівником деканату (closed = true). Розподіл у групу другого вибору виконується окремим механізмом після закриття запису.";
        }
        else if (!EnrollmentDomainRules.IsWithinEnrollmentWindow(nowUtc, opensAt, closesAt))
        {
            finalStatus = "rejected";
            rejectionReason = $"Запит поза часовим вікном запису на групу '{item.GroupId}'.";
        }
        else
        {
            // 3. Крок 3 канонічного порядку блокувань: перевірка запису Web (enrollment FOR UPDATE) та відомості деканату
            Guid? matchingWebEnrollmentId = null;
            Guid? otherGroupWebEnrollmentId = null;

            if (studentExists)
            {
                var activeEnrs = await _enrollmentRepo.GetActiveEnrollmentsForDisciplineAsync(
                    item.StudentId, disciplineId, conn, tx, cancellationToken);

                foreach (var (enrId, enrGroup) in activeEnrs)
                {
                    if (string.Equals(enrGroup, item.GroupId, StringComparison.Ordinal))
                    {
                        matchingWebEnrollmentId = enrId;
                    }
                    else
                    {
                        otherGroupWebEnrollmentId = enrId;
                    }
                }
            }

            var (alreadyInRosterForDiscipline, rosterOtherDisciplineCount, rosterOtherEcts) =
                await _decanateRepo.GetStudentRosterStatsAsync(item.StudentId, disciplineId, conn, tx, cancellationToken);

            int groupRosterCount = await _decanateRepo.GetGroupRosterCountAsync(item.GroupId, conn, tx, cancellationToken);

            // Безпосередня перевірка обмежень max_disciplines (K) та сумарної кількості ЄКТС (<= 30.0) у логіці РПО:
            int countBeforeCurrent = matchingWebEnrollmentId.HasValue
                ? Math.Max(Math.Max(0, studentActiveCount - 1), rosterOtherDisciplineCount)
                : Math.Max(studentActiveCount, rosterOtherDisciplineCount);

            decimal ectsBeforeCurrent = matchingWebEnrollmentId.HasValue
                ? Math.Max(Math.Max(0.0m, studentActiveEcts - disciplineEcts), rosterOtherEcts)
                : Math.Max(studentActiveEcts, rosterOtherEcts);

            if (alreadyInRosterForDiscipline || otherGroupWebEnrollmentId.HasValue)
            {
                finalStatus = "rejected";
                rejectionReason = $"Студент '{item.StudentId}' вже зареєстрований у відомості деканату або має активний запис на дисципліну '{disciplineId}'.";
            }
            else if (!EnrollmentDomainRules.CanEnrollWithDisciplineLimit(countBeforeCurrent, maxDisciplines))
            {
                finalStatus = "rejected";
                rejectionReason = $"Перевищено ліміт активних дисциплін студента '{item.StudentId}' ({countBeforeCurrent} із {maxDisciplines} дозволених).";
            }
            else if (!EnrollmentDomainRules.CanEnrollWithEcts(ectsBeforeCurrent, disciplineEcts))
            {
                decimal projectedEcts = ectsBeforeCurrent + disciplineEcts;
                finalStatus = "rejected";
                rejectionReason = $"Перевищено семестровий ліміт кредитів ЄКТС студента '{item.StudentId}' ({projectedEcts:0.0} > {EnrollmentDomainRules.MaxSemesterEcts:0.0}).";
            }
            else if (studentExists && !matchingWebEnrollmentId.HasValue)
            {
                // Первинний запис студента в групу та резервування місця під час відкритого вікна — виключна відповідальність Web
                finalStatus = "rejected";
                rejectionReason = $"Відсутній первинний запис студента '{item.StudentId}' у групу '{item.GroupId}' через Web-сервіс (POST /v1/enrollments). Первинний запис і зменшення вільних місць виконує виключно Web.";
            }
            else if (groupRosterCount >= seats)
            {
                finalStatus = "rejected";
                rejectionReason = $"Вичерпано ліміт місць у відомості деканату для навчальної групи '{item.GroupId}' (зафіксовано {groupRosterCount}/{seats}).";
            }
            else
            {
                // Предметний ефект РПО (С10 §3.12): фіксація результату обробки заявки у відомості деканату за request_id
                // БЕЗ повторного зменшення discipline_group.available!
                await _decanateRepo.InsertRosterEntryAsync(
                    item.Id,
                    item.StudentId,
                    item.GroupId,
                    disciplineId,
                    disciplineEcts,
                    matchingWebEnrollmentId,
                    "primary_roster",
                    nowUtc,
                    conn,
                    tx,
                    cancellationToken);

                if (matchingWebEnrollmentId.HasValue)
                {
                    await _enrollmentRepo.ConfirmEnrollmentHeldAsync(matchingWebEnrollmentId.Value, conn, tx, cancellationToken);
                }

                finalStatus = "done";
                assignedGroupId = item.GroupId;
            }
        }

        // О1.5: Фіксуємо монотонну позначку завершення ДО коміту кінцевого статусу в БД
        var finishedAtUtc = _metrics.MarkProcessingFinished(item);

        await _requestRepo.UpdateStatusAsync(
            item.Id,
            finalStatus,
            rejectionReason,
            assignedGroupId,
            finishedAtUtc,
            conn,
            tx,
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return (finalStatus, rejectionReason);
    }
}
