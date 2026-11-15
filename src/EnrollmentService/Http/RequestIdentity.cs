using EnrollmentService.Repositories;

namespace EnrollmentService.Http;

public static class RequestIdentity
{
    public static bool TryGetAuthenticatedUser(HttpContext ctx, out AuthenticatedWebUser? webUser)
    {
        if (ctx.Items.TryGetValue("WebUser", out var obj) && obj is AuthenticatedWebUser user)
        {
            webUser = user;
            return true;
        }

        webUser = null;
        return false;
    }

    public static bool TryRequireAuthenticatedUser(
        HttpContext ctx,
        ServiceOptions opts,
        out AuthenticatedWebUser? webUser,
        out IResult? problemResult)
    {
        if (!TryGetAuthenticatedUser(ctx, out webUser) || webUser == null)
        {
            problemResult = ProblemDetailsMapper.CreateProblemResult(
                ctx, opts,
                StatusCodes.Status401Unauthorized,
                "Неавторизований доступ",
                "Необхідна автентифікація користувача (Authorization: Bearer <token>).",
                "unauthorized");
            return false;
        }

        problemResult = null;
        return true;
    }

    public static bool TryRequireCallerStudentId(
        HttpContext ctx,
        ServiceOptions opts,
        out string studentId,
        out IResult? problemResult)
    {
        studentId = string.Empty;

        // Web ЛР3 (О3.1): Повна відмова від довіри до заголовка X-User-Id.
        // Особа студента визначається виключно з перевіреного серверного токена у контексті запиту.
        if (!TryRequireAuthenticatedUser(ctx, opts, out var webUser, out problemResult))
        {
            return false;
        }

        if (!string.Equals(webUser!.Role, "user", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(webUser.StudentId))
        {
            problemResult = ProblemDetailsMapper.CreateProblemResult(
                ctx, opts,
                StatusCodes.Status403Forbidden,
                "Операція доступна лише студентам",
                "Поточний обліковий запис не прив'язаний до профілю студента (потрібна роль 'user').",
                "student_role_required");
            return false;
        }

        studentId = webUser.StudentId;
        problemResult = null;
        return true;
    }

    public static bool TryRequireStaffRole(
        HttpContext ctx,
        ServiceOptions opts,
        out AuthenticatedWebUser? staffUser,
        out IResult? problemResult)
    {
        if (!TryRequireAuthenticatedUser(ctx, opts, out staffUser, out problemResult))
        {
            return false;
        }

        if (!string.Equals(staffUser!.Role, "staff", StringComparison.Ordinal))
        {
            problemResult = ProblemDetailsMapper.CreateProblemResult(
                ctx, opts,
                StatusCodes.Status403Forbidden,
                "Доступ заборонено",
                "Ця функція доступна виключно працівникам деканату з роллю 'staff'.",
                "insufficient_role");
            return false;
        }

        problemResult = null;
        return true;
    }
}
