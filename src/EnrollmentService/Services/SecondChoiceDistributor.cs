using EnrollmentService.Domain;
using EnrollmentService.Repositories;
using Npgsql;

namespace EnrollmentService.Services;

public sealed class SecondChoiceDistributor : ISecondChoiceDistributor
{
    private readonly IGroupRepository _groupRepo;
    private readonly IEnrollmentRepository _enrollmentRepo;
    private readonly IEnrollmentRequestRepository _requestRepo;
    private readonly IDecanateRepository _decanateRepo;
    private readonly IDbConnectionFactory _connectionFactory;

    public SecondChoiceDistributor(
        IGroupRepository groupRepo,
        IEnrollmentRepository enrollmentRepo,
        IEnrollmentRequestRepository requestRepo,
        IDecanateRepository decanateRepo,
        IDbConnectionFactory connectionFactory)
    {
        _groupRepo = groupRepo;
        _enrollmentRepo = enrollmentRepo;
        _requestRepo = requestRepo;
        _decanateRepo = decanateRepo;
        _connectionFactory = connectionFactory;
    }

    public async Task<ServiceResult<SecondChoiceDistributionResponseDto>> DistributeSecondChoiceAsync(
        string primaryGroupId,
        SecondChoiceDistributionRequestDto? request,
        CancellationToken cancellationToken = default)
    {
        var primaryGroup = await _groupRepo.GetGroupByIdAsync(primaryGroupId, cancellationToken);
        if (primaryGroup == null)
        {
            return ServiceResult<SecondChoiceDistributionResponseDto>.Failure(
                StatusCodes.Status404NotFound,
                "group_not_found",
                "Основну групу не знайдено",
                $"Навчальна група '{primaryGroupId}' відсутня в базі даних.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        bool isEffectivelyClosed = primaryGroup.Closed || nowUtc >= primaryGroup.ClosesAt;
        if (!isEffectivelyClosed)
        {
            return ServiceResult<SecondChoiceDistributionResponseDto>.Failure(
                StatusCodes.Status409Conflict,
                "primary_group_still_open",
                "Запис в основну групу ще відкрито",
                $"Розподіл за другим вибором для групи '{primaryGroupId}' дозволений виключно після закриття запису (POST /v1/groups/{primaryGroupId}/close).");
        }

        Guid? filterRequestId = null;
        await using var conn = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        if (request != null && (!string.IsNullOrWhiteSpace(request.StudentId) || request.RequestId.HasValue))
        {
            if (string.IsNullOrWhiteSpace(request.StudentId) || string.IsNullOrWhiteSpace(request.SecondChoiceGroupId))
            {
                return ServiceResult<SecondChoiceDistributionResponseDto>.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    "missing_second_choice_fields",
                    "Некоректні параметри розподілу другого вибору",
                    "При точковому розподілі необхідно передати поля student_id та second_choice_group_id.");
            }

            string reqStudentId = request.StudentId.Trim();
            string reqSecondGroup = request.SecondChoiceGroupId.Trim();
            Guid targetReqId = request.RequestId ?? Guid.NewGuid();

            await _requestRepo.UpsertSecondChoiceRequestAsync(
                targetReqId,
                reqStudentId,
                primaryGroupId,
                reqSecondGroup,
                nowUtc,
                conn,
                cancellationToken);

            filterRequestId = targetReqId;
        }

        var candidates = await _requestRepo.GetSecondChoiceCandidatesAsync(
            primaryGroupId,
            filterRequestId,
            conn,
            cancellationToken);

        var results = new List<SecondChoiceDistributionItemDto>();
        int distributedCount = 0;
        int rejectedCount = 0;

        foreach (var candidate in candidates)
        {
            var itemResult = await DistributeSingleCandidateAsync(
                primaryGroupId,
                candidate.RequestId,
                candidate.StudentId,
                candidate.SecondChoiceGroupId,
                conn,
                cancellationToken);

            results.Add(itemResult);
            if (string.Equals(itemResult.Status, "done", StringComparison.Ordinal))
            {
                distributedCount++;
            }
            else
            {
                rejectedCount++;
            }
        }

        var responseDto = new SecondChoiceDistributionResponseDto(
            primaryGroupId,
            true,
            distributedCount,
            rejectedCount,
            results);

        return ServiceResult<SecondChoiceDistributionResponseDto>.Ok(responseDto);
    }

    private async Task<SecondChoiceDistributionItemDto> DistributeSingleCandidateAsync(
        string primaryGroupId,
        Guid requestId,
        string studentId,
        string secondChoiceGroupId,
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;

        // Ідемпотентна перевірка: чи вже розподілена ця заявка у відомість деканату
        var existingAssignment = await _decanateRepo.GetRosterAssignmentForRequestAsync(requestId, conn, tx, cancellationToken);
        if (existingAssignment.HasValue)
        {
            await tx.CommitAsync(cancellationToken);
            return new SecondChoiceDistributionItemDto(
                requestId,
                studentId,
                primaryGroupId,
                existingAssignment.Value.GroupId,
                "done",
                existingAssignment.Value.EnrollmentId?.ToString("D"),
                true,
                null);
        }

        // 1. Крок 1 канонічного порядку блокувань: student FOR UPDATE
        var student = await _enrollmentRepo.GetStudentForUpdateAsync(studentId, conn, tx, cancellationToken);
        bool studentExists = student != null;
        int maxDisciplines = student?.MaxDisciplines ?? 5;
        int activeCount = student?.ActiveCount ?? 0;
        decimal activeEcts = student?.ActiveEcts ?? 0.0m;

        // 2. Крок 2 канонічного порядку блокувань: discipline_group FOR UPDATE (група другого вибору)
        var targetGroupWithDisc = await _groupRepo.GetGroupWithDisciplineForUpdateAsync(secondChoiceGroupId, conn, tx, cancellationToken);
        bool targetGroupExists = targetGroupWithDisc.HasValue;
        string disciplineId = targetGroupWithDisc?.Group.DisciplineId ?? string.Empty;
        decimal disciplineEcts = targetGroupWithDisc?.DisciplineEcts ?? 0.0m;
        int seats = targetGroupWithDisc?.Group.Seats ?? 0;
        int available = targetGroupWithDisc?.Group.Available ?? 0;
        DateTimeOffset opensAt = targetGroupWithDisc?.Group.OpensAt ?? default;
        DateTimeOffset closesAt = targetGroupWithDisc?.Group.ClosesAt ?? default;
        bool closed = targetGroupWithDisc?.Group.Closed ?? false;

        string? rejectReason = null;

        if (!targetGroupExists)
        {
            rejectReason = $"Групу другого вибору '{secondChoiceGroupId}' не знайдено.";
        }
        else if (string.Equals(primaryGroupId, secondChoiceGroupId, StringComparison.Ordinal))
        {
            rejectReason = $"Група другого вибору '{secondChoiceGroupId}' збігається із закритою основною групою.";
        }
        else if (closed)
        {
            rejectReason = $"Група другого вибору '{secondChoiceGroupId}' також закрита (closed = true).";
        }
        else if (!EnrollmentDomainRules.IsWithinEnrollmentWindow(nowUtc, opensAt, closesAt))
        {
            rejectReason = $"Група другого вибору '{secondChoiceGroupId}' перебуває поза активним вікном запису.";
        }
        else
        {
            bool alreadyEnrolledInDiscipline = false;
            if (studentExists)
            {
                alreadyEnrolledInDiscipline = await _enrollmentRepo.HasActiveEnrollmentForDisciplineAsync(
                    studentId, disciplineId, conn, tx, cancellationToken);
            }

            var (alreadyInRoster, rosterOtherCount, rosterOtherEcts) =
                await _decanateRepo.GetStudentRosterStatsAsync(studentId, disciplineId, conn, tx, cancellationToken);

            int effectiveCount = Math.Max(activeCount, rosterOtherCount);
            decimal effectiveEcts = Math.Max(activeEcts, rosterOtherEcts);

            if (alreadyEnrolledInDiscipline || alreadyInRoster)
            {
                rejectReason = $"Студент '{studentId}' вже має активний запис на дисципліну '{disciplineId}'.";
            }
            else if (!EnrollmentDomainRules.CanEnrollWithDisciplineLimit(effectiveCount, maxDisciplines))
            {
                rejectReason = $"Перевищено ліміт активних дисциплін студента '{studentId}' ({effectiveCount}/{maxDisciplines}).";
            }
            else if (!EnrollmentDomainRules.CanEnrollWithEcts(effectiveEcts, disciplineEcts))
            {
                rejectReason = $"Перевищено семестровий ліміт кредитів ЄКТС студента '{studentId}' ({effectiveEcts + disciplineEcts:0.0} > {EnrollmentDomainRules.MaxSemesterEcts:0.0}).";
            }
            else if (available <= 0)
            {
                rejectReason = $"У групі другого вибору '{secondChoiceGroupId}' немає вільних місць (зайнято {seats}/{seats}).";
            }
        }

        if (rejectReason != null)
        {
            await _requestRepo.UpdateStatusAsync(
                requestId,
                "rejected",
                rejectReason,
                null,
                nowUtc,
                conn,
                tx,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return new SecondChoiceDistributionItemDto(
                requestId,
                studentId,
                primaryGroupId,
                secondChoiceGroupId,
                "rejected",
                null,
                false,
                rejectReason);
        }

        // Тією самою атомарною умовою місць зменшуємо available у групі другого вибору (С10 §3.12 п. 6)
        bool decremented = await _groupRepo.DecrementAvailableAsync(secondChoiceGroupId, conn, tx, cancellationToken);
        if (!decremented)
        {
            string noSeatsReason = $"У групі другого вибору '{secondChoiceGroupId}' вичерпано вільні місця.";
            await _requestRepo.UpdateStatusAsync(
                requestId,
                "rejected",
                noSeatsReason,
                null,
                nowUtc,
                conn,
                tx,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return new SecondChoiceDistributionItemDto(
                requestId,
                studentId,
                primaryGroupId,
                secondChoiceGroupId,
                "rejected",
                null,
                false,
                noSeatsReason);
        }

        Guid? createdEnrollmentId = null;
        if (studentExists)
        {
            await _enrollmentRepo.UpdateStudentStatsAsync(studentId, +1, disciplineEcts, conn, tx, cancellationToken);

            Guid enrId = Guid.NewGuid();
            createdEnrollmentId = enrId;
            await _enrollmentRepo.CreateEnrollmentAsync(
                enrId,
                studentId,
                secondChoiceGroupId,
                disciplineId,
                "confirmed",
                1,
                nowUtc,
                nowUtc.AddMinutes(15),
                conn,
                tx,
                cancellationToken);
        }

        await _decanateRepo.InsertRosterEntryAsync(
            requestId,
            studentId,
            secondChoiceGroupId,
            disciplineId,
            disciplineEcts,
            createdEnrollmentId,
            "second_choice",
            nowUtc,
            conn,
            tx,
            cancellationToken);

        await _requestRepo.UpdateStatusAsync(
            requestId,
            "done",
            null,
            secondChoiceGroupId,
            nowUtc,
            conn,
            tx,
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new SecondChoiceDistributionItemDto(
            requestId,
            studentId,
            primaryGroupId,
            secondChoiceGroupId,
            "done",
            createdEnrollmentId?.ToString("D"),
            true,
            null);
    }
}
