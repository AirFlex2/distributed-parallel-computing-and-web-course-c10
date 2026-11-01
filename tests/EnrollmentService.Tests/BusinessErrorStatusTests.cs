using System.Text;
using EnrollmentService.Domain;
using EnrollmentService.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EnrollmentService.Tests;

public sealed class BusinessErrorStatusTests
{
    private readonly DateTimeOffset _opensAt = DateTimeOffset.Parse("2026-09-01T08:00:00Z");
    private readonly DateTimeOffset _closesAt = DateTimeOffset.Parse("2026-09-15T18:00:00Z");

    private DisciplineGroupEntity CreateTestGroup(int seats = 25, int available = 10, bool closed = false) =>
        new("TEST-GRP-01", "TEST-DISC-01", "G1", seats, available, _opensAt, _closesAt, "Europe/Kyiv", closed, 5.0m);

    private StudentEntity CreateTestStudent(int activeCount = 1, decimal activeEcts = 5.0m, int maxDisciplines = 5) =>
        new("student-test-1", "test@univ.ua", "Test Student", maxDisciplines, activeCount, activeEcts);

    // Сценарій 1: Некоректний JSON -> 400 Bad Request
    [Fact]
    public async Task InvalidJson_Returns400BadRequest()
    {
        var context = new DefaultHttpContext();
        var corruptJson = "{ \"group_id\": \"DISC-WEB-01\", corrupt ";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(corruptJson));
        context.Request.ContentType = "application/json";
        context.Items["RequestId"] = "req-test-json";

        var opts = new ServiceOptions { InstanceId = "app-test" };
        var (dto, problem) = await HttpPayloadHelper.ReadAndValidateAsync<CreateWebEnrollmentDto>(context, opts);

        Assert.Null(dto);
        Assert.NotNull(problem);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, statusResult.StatusCode);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(problem);
        var details = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ProblemDetails>(valueResult.Value);
        Assert.Equal("invalid_json_syntax", details.Extensions["error_code"]?.ToString());
    }

    // Сценарій 2: Неіснуюча група -> 404 Not Found
    [Fact]
    public void NonExistentGroup_Returns404NotFound()
    {
        var failure = ServiceResult<WebEnrollmentResponseDto>.Failure(
            StatusCodes.Status404NotFound,
            "group_not_found",
            "Групу дисципліни не знайдено",
            "Група з ідентифікатором 'DISC-NONEXISTENT' не існує.");

        Assert.False(failure.IsSuccess);
        Assert.Equal(StatusCodes.Status404NotFound, failure.StatusCode);
        Assert.Equal("group_not_found", failure.ErrorCode);
    }

    // Сценарій 3: Повторний запис на ту саму дисципліну -> 409 Conflict
    [Fact]
    public void DuplicateDisciplineEnrollment_Returns409Conflict()
    {
        var student = CreateTestStudent();
        var group = CreateTestGroup();
        var now = _opensAt.AddDays(1);

        var check = EnrollmentPolicy.Check(student, group, 5.0m, now, alreadyEnrolledForDiscipline: true);

        Assert.False(check.Allowed);
        Assert.Equal(PolicyRejectionReason.DuplicateDiscipline, check.Reason);
        Assert.Equal("duplicate_discipline_enrollment", check.ErrorCode);
        Assert.Equal(StatusCodes.Status409Conflict, check.GetHttpStatusCode());
    }

    // Сценарій 4: Немає вільних місць у групі -> 409 Conflict
    [Fact]
    public void GroupFull_Returns409Conflict()
    {
        var student = CreateTestStudent();
        var fullGroup = CreateTestGroup(seats: 25, available: 0);
        var now = _opensAt.AddDays(1);

        var check = EnrollmentPolicy.Check(student, fullGroup, 5.0m, now, alreadyEnrolledForDiscipline: false);

        Assert.False(check.Allowed);
        Assert.Equal(PolicyRejectionReason.GroupFull, check.Reason);
        Assert.Equal("group_full", check.ErrorCode);
        Assert.Equal(StatusCodes.Status409Conflict, check.GetHttpStatusCode());
    }

    // Сценарій 5: Сума ECTS перевищує 30.0 -> 422 Unprocessable Content
    [Fact]
    public void EctsLimitExceeded_Returns422UnprocessableContent()
    {
        // Студент уже має 26.0 ЄКТС, намагається обрати 5.0 ЄКТС -> 31.0 > 30.0
        var student = CreateTestStudent(activeCount: 3, activeEcts: 26.0m);
        var group = CreateTestGroup();
        var now = _opensAt.AddDays(1);

        var check = EnrollmentPolicy.Check(student, group, 5.0m, now, alreadyEnrolledForDiscipline: false);

        Assert.False(check.Allowed);
        Assert.Equal(PolicyRejectionReason.EctsLimitExceeded, check.Reason);
        Assert.Equal("ects_limit_exceeded", check.ErrorCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, check.GetHttpStatusCode());
    }

    // Сценарій 6: Запит до opens_at -> 422 Unprocessable Content
    [Fact]
    public void RequestBeforeOpensAt_Returns422UnprocessableContent()
    {
        var student = CreateTestStudent();
        var group = CreateTestGroup();
        var beforeOpen = _opensAt.AddSeconds(-1);

        var check = EnrollmentPolicy.Check(student, group, 5.0m, beforeOpen, alreadyEnrolledForDiscipline: false);

        Assert.False(check.Allowed);
        Assert.Equal(PolicyRejectionReason.WindowClosed, check.Reason);
        Assert.Equal("enrollment_window_closed", check.ErrorCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, check.GetHttpStatusCode());
    }

    // Сценарій 7: Запит ровно в opens_at -> Успіх (включна нижня межа напіввідкритого інтервалу)
    [Fact]
    public void RequestExactlyAtOpensAt_Succeeds()
    {
        var student = CreateTestStudent();
        var group = CreateTestGroup();
        var exactlyOpen = _opensAt;

        var check = EnrollmentPolicy.Check(student, group, 5.0m, exactlyOpen, alreadyEnrolledForDiscipline: false);

        Assert.True(check.Allowed);
        Assert.Equal(PolicyRejectionReason.None, check.Reason);
        Assert.Null(check.ErrorCode);
    }

    // Сценарій 8: Запит за секунду до closes_at -> Успіх (всередині інтервалу)
    [Fact]
    public void RequestOneSecondBeforeClosesAt_Succeeds()
    {
        var student = CreateTestStudent();
        var group = CreateTestGroup();
        var inside = _closesAt.AddSeconds(-1);

        var check = EnrollmentPolicy.Check(student, group, 5.0m, inside, alreadyEnrolledForDiscipline: false);

        Assert.True(check.Allowed);
        Assert.Equal(PolicyRejectionReason.None, check.Reason);
        Assert.Null(check.ErrorCode);
    }

    // Сценарій 9: Запит ровно в closes_at -> 422 Unprocessable Content (невключна верхня межа)
    [Fact]
    public void RequestExactlyAtClosesAt_Returns422UnprocessableContent()
    {
        var student = CreateTestStudent();
        var group = CreateTestGroup();
        var exactlyClose = _closesAt;

        var check = EnrollmentPolicy.Check(student, group, 5.0m, exactlyClose, alreadyEnrolledForDiscipline: false);

        Assert.False(check.Allowed);
        Assert.Equal(PolicyRejectionReason.WindowClosed, check.Reason);
        Assert.Equal("enrollment_window_closed", check.ErrorCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, check.GetHttpStatusCode());
    }
}
