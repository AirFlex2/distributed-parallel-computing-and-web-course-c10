namespace EnrollmentService.Http;

public static class RequestIdentity
{
    public static bool TryRequireCallerStudentId(
        HttpContext ctx,
        ServiceOptions opts,
        out string studentId,
        out IResult? problemResult)
    {
        studentId = string.Empty;
        problemResult = null;

        if (!ctx.Request.Headers.TryGetValue("X-User-Id", out var headerVals))
        {
            problemResult = ProblemDetailsMapper.CreateProblemResult(
                ctx, opts,
                StatusCodes.Status400BadRequest,
                "Відсутній обов'язковий заголовок X-User-Id",
                "До введення автентифікації в ЛР3 ідентифікатор студента має обов'язково передаватися у заголовку X-User-Id.",
                "missing_user_header");
            return false;
        }

        string raw = headerVals.ToString().Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 64)
        {
            problemResult = ProblemDetailsMapper.CreateProblemResult(
                ctx, opts,
                StatusCodes.Status400BadRequest,
                "Некоректний заголовок X-User-Id",
                "Заголовок X-User-Id не може бути порожнім або перевищувати 64 символи.",
                "empty_user_header");
            return false;
        }

        studentId = raw;
        return true;
    }
}
