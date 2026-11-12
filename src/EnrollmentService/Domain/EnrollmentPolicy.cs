namespace EnrollmentService.Domain;

using Microsoft.AspNetCore.Http;
using EnrollmentService.Services;

public enum PolicyRejectionReason
{
    None = 0,
    GroupClosed,
    WindowClosed,
    DuplicateDiscipline,
    DisciplineLimitExceeded,
    EctsLimitExceeded,
    GroupFull
}

public static class EnrollmentPolicy
{
    public sealed record CheckResult(
        bool Allowed,
        PolicyRejectionReason Reason,
        string? ErrorCode,
        string? RejectionReason)
    {
        public int GetHttpStatusCode() => Reason switch
        {
            PolicyRejectionReason.GroupFull => StatusCodes.Status409Conflict,
            PolicyRejectionReason.DuplicateDiscipline => StatusCodes.Status409Conflict,
            PolicyRejectionReason.EctsLimitExceeded => StatusCodes.Status422UnprocessableEntity,
            PolicyRejectionReason.DisciplineLimitExceeded => StatusCodes.Status422UnprocessableEntity,
            PolicyRejectionReason.WindowClosed => StatusCodes.Status422UnprocessableEntity,
            PolicyRejectionReason.GroupClosed => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status422UnprocessableEntity
        };

        public string GetErrorTitle() => Reason switch
        {
            PolicyRejectionReason.GroupFull => "У групі немає вільних місць",
            PolicyRejectionReason.DuplicateDiscipline => "Повторний запис на дисципліну",
            PolicyRejectionReason.EctsLimitExceeded => "Перевищено семестровий ліміт кредитів ЄКТС",
            PolicyRejectionReason.DisciplineLimitExceeded => "Перевищено ліміт кількості дисциплін",
            PolicyRejectionReason.WindowClosed => "Вікно запису закрито",
            PolicyRejectionReason.GroupClosed => "Групу закрито деканатом",
            _ => "Відхилено правилами запису"
        };
    }

    public static CheckResult Check(
        StudentEntity student,
        DisciplineGroupEntity group,
        decimal disciplineEcts,
        DateTimeOffset nowUtc,
        bool alreadyEnrolledForDiscipline)
    {
        if (group.Closed)
        {
            return new(false, PolicyRejectionReason.GroupClosed, "group_closed", $"Навчальна група '{group.Id}' закрита працівником деканату (closed = true).");
        }

        if (!EnrollmentDomainRules.IsWithinEnrollmentWindow(nowUtc, group.OpensAt, group.ClosesAt))
        {
            return new(false, PolicyRejectionReason.WindowClosed, "enrollment_window_closed", $"Запит поза часовим вікном запису на групу '{group.Id}'.");
        }

        if (alreadyEnrolledForDiscipline)
        {
            return new(false, PolicyRejectionReason.DuplicateDiscipline, "duplicate_discipline_enrollment", $"Студент '{student.Id}' вже має активний запис на дану дисципліну.");
        }

        if (!EnrollmentDomainRules.CanEnrollWithDisciplineLimit(student.ActiveCount, student.MaxDisciplines))
        {
            return new(false, PolicyRejectionReason.DisciplineLimitExceeded, "discipline_limit_exceeded", $"Перевищено ліміт активних дисциплін студента '{student.Id}' ({student.ActiveCount} із {student.MaxDisciplines} дозволених).");
        }

        if (!EnrollmentDomainRules.CanEnrollWithEcts(student.ActiveEcts, disciplineEcts))
        {
            decimal projectedEcts = student.ActiveEcts + disciplineEcts;
            return new(false, PolicyRejectionReason.EctsLimitExceeded, "ects_limit_exceeded", $"Перевищено семестровий ліміт кредитів ЄКТС студента '{student.Id}' ({projectedEcts:0.0} > {EnrollmentDomainRules.MaxSemesterEcts:0.0}).");
        }

        if (group.Available <= 0)
        {
            return new(false, PolicyRejectionReason.GroupFull, "group_full", $"Вичерпано ліміт місць у навчальній групі '{group.Id}' (доступно 0 місць).");
        }

        return new(true, PolicyRejectionReason.None, null, null);
    }
}
