using System;
using EnrollmentService.Domain;
using EnrollmentService.Services;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class DomainAndQueueTests
{
    // Web Д1.1 та Д1.5: Чиста функція перевірки часового вікна [opens_at, closes_at)
    [Fact]
    public void IsWithinEnrollmentWindow_EnforcesHalfOpenIntervalBoundaries()
    {
        var opensAt = DateTimeOffset.Parse("2026-09-01T07:00:00Z");
        var closesAt = DateTimeOffset.Parse("2026-09-15T15:00:00Z");

        // За 1 мс до opens_at -> false
        Assert.False(EnrollmentDomainRules.IsWithinEnrollmentWindow(opensAt.AddMilliseconds(-1), opensAt, closesAt));

        // Рівно в момент opens_at (включна нижня межа) -> true
        Assert.True(EnrollmentDomainRules.IsWithinEnrollmentWindow(opensAt, opensAt, closesAt));

        // Всередині інтервалу (у часовому поясі Europe/Kyiv UTC+3) -> true
        var kyivInside = DateTimeOffset.Parse("2026-09-01T10:00:00+03:00");
        Assert.True(EnrollmentDomainRules.IsWithinEnrollmentWindow(kyivInside, opensAt, closesAt));

        // За 1 мс до closes_at -> true
        Assert.True(EnrollmentDomainRules.IsWithinEnrollmentWindow(closesAt.AddMilliseconds(-1), opensAt, closesAt));

        // Рівно в момент closes_at (невключна верхня межа) -> false
        Assert.False(EnrollmentDomainRules.IsWithinEnrollmentWindow(closesAt, opensAt, closesAt));
    }

    // Web Д1.5: Чиста функція перевірки точних кредитів ЄКТС (decimal / numeric(3,1), <= 30.0)
    [Fact]
    public void CanEnrollWithEcts_AllowsExact30AndRejects30Point5()
    {
        Assert.True(EnrollmentDomainRules.CanEnrollWithEcts(20.0m, 10.0m));
        Assert.True(EnrollmentDomainRules.CanEnrollWithEcts(29.5m, 0.5m));
        Assert.False(EnrollmentDomainRules.CanEnrollWithEcts(20.0m, 10.5m));
        Assert.False(EnrollmentDomainRules.CanEnrollWithEcts(30.0m, 0.1m));
    }

    // Web О1.1 та РПО О1.4: Чиста функція перевірки максимальної кількості дисциплін K (за замовчуванням K = 5)
    [Fact]
    public void CanEnrollWithDisciplineLimit_EnforcesMaxDisciplinesK()
    {
        Assert.True(EnrollmentDomainRules.CanEnrollWithDisciplineLimit(0, 5));
        Assert.True(EnrollmentDomainRules.CanEnrollWithDisciplineLimit(4, 5));
        Assert.False(EnrollmentDomainRules.CanEnrollWithDisciplineLimit(5, 5));
        Assert.False(EnrollmentDomainRules.CanEnrollWithDisciplineLimit(6, 5));
    }

    // Web Д1.5: Перевірка переходу через межу доби (day-boundary crossing) у часовому поясі університету Europe/Kyiv
    [Fact]
    public void UniversityTimeZone_DayBoundaryCrossing_AccuratelyDetectsMidnightTransitionAndWindow()
    {
        // Літній час у Києві (UTC+03:00):
        // 2026-09-01T20:59:59Z -> 2026-09-01T23:59:59+03:00 (ще 1 вересня за Києвом)
        // 2026-09-01T21:00:00Z -> 2026-09-02T00:00:00+03:00 (вже 2 вересня за Києвом, хоча за UTC все ще 2026-09-01)
        var beforeKyivMidnightUtc = DateTimeOffset.Parse("2026-09-01T20:59:59Z");
        var atKyivMidnightUtc = DateTimeOffset.Parse("2026-09-01T21:00:00Z");

        Assert.Equal("2026-09-01T23:59:59+03:00", EnrollmentDomainRules.FormatInUniversityTimeZone(beforeKyivMidnightUtc));
        Assert.Equal("2026-09-02T00:00:00+03:00", EnrollmentDomainRules.FormatInUniversityTimeZone(atKyivMidnightUtc));

        Assert.Equal(new DateOnly(2026, 9, 1), EnrollmentDomainRules.GetUniversityLocalDate(beforeKyivMidnightUtc));
        Assert.Equal(new DateOnly(2026, 9, 2), EnrollmentDomainRules.GetUniversityLocalDate(atKyivMidnightUtc));
        Assert.True(EnrollmentDomainRules.HasCrossedUniversityDayBoundary(beforeKyivMidnightUtc, atKyivMidnightUtc));

        // Зимовий час у Києві (UTC+02:00):
        // 2026-12-10T21:59:59Z -> 2026-12-10T23:59:59+02:00 (10 грудня за Києвом)
        // 2026-12-10T22:00:00Z -> 2026-12-11T00:00:00+02:00 (11 грудня за Києвом)
        var winterBeforeMidnightUtc = DateTimeOffset.Parse("2026-12-10T21:59:59Z");
        var winterAtMidnightUtc = DateTimeOffset.Parse("2026-12-10T22:00:00Z");

        Assert.Equal("2026-12-10T23:59:59+02:00", EnrollmentDomainRules.FormatInUniversityTimeZone(winterBeforeMidnightUtc));
        Assert.Equal("2026-12-11T00:00:00+02:00", EnrollmentDomainRules.FormatInUniversityTimeZone(winterAtMidnightUtc));
        Assert.True(EnrollmentDomainRules.HasCrossedUniversityDayBoundary(winterBeforeMidnightUtc, winterAtMidnightUtc));

        // Перевірка вікна запису, яке закривається рівно на межі доби за Києвом (2026-09-02T00:00:00+03:00)
        var windowOpen = DateTimeOffset.Parse("2026-09-01T10:00:00+03:00");
        var windowCloseAtMidnight = DateTimeOffset.Parse("2026-09-02T00:00:00+03:00");

        Assert.True(EnrollmentDomainRules.IsWithinEnrollmentWindow(beforeKyivMidnightUtc, windowOpen, windowCloseAtMidnight));
        Assert.False(EnrollmentDomainRules.IsWithinEnrollmentWindow(atKyivMidnightUtc, windowOpen, windowCloseAtMidnight));
    }

    // Web Д1.5: Перевірка зміни часового зміщення (DST offset transition UTC+02:00 <-> UTC+03:00 та порівняння різних зміщень)
    [Fact]
    public void UniversityTimeZone_OffsetChange_HandlesSpringForwardAndAutumnBackAndCrossOffsetWindow()
    {
        // 1. Весняний перехід на літній час у 2026 році (остання неділя березня: 2026-03-29 о 01:00 UTC):
        // зміщення змінюється з +02:00 (EET) на +03:00 (EEST), локальний годинник переводиться з 02:59:59 на 04:00:00
        var beforeSpringForwardUtc = DateTimeOffset.Parse("2026-03-29T00:59:59Z");
        var atSpringForwardUtc = DateTimeOffset.Parse("2026-03-29T01:00:00Z");

        Assert.Equal(TimeSpan.FromHours(2), EnrollmentDomainRules.GetUniversityTimeZoneOffset(beforeSpringForwardUtc));
        Assert.Equal(TimeSpan.FromHours(3), EnrollmentDomainRules.GetUniversityTimeZoneOffset(atSpringForwardUtc));
        Assert.Equal("2026-03-29T02:59:59+02:00", EnrollmentDomainRules.FormatInUniversityTimeZone(beforeSpringForwardUtc));
        Assert.Equal("2026-03-29T04:00:00+03:00", EnrollmentDomainRules.FormatInUniversityTimeZone(atSpringForwardUtc));

        // 2. Осінній перехід на зимовий час у 2026 році (остання неділя жовтня: 2026-10-25 о 01:00 UTC):
        // зміщення змінюється з +03:00 (EEST) на +02:00 (EET), локальний годинник переводиться з 03:59:59+03:00 на 03:00:00+02:00
        var beforeAutumnBackUtc = DateTimeOffset.Parse("2026-10-25T00:59:59Z");
        var atAutumnBackUtc = DateTimeOffset.Parse("2026-10-25T01:00:00Z");

        Assert.Equal(TimeSpan.FromHours(3), EnrollmentDomainRules.GetUniversityTimeZoneOffset(beforeAutumnBackUtc));
        Assert.Equal(TimeSpan.FromHours(2), EnrollmentDomainRules.GetUniversityTimeZoneOffset(atAutumnBackUtc));
        Assert.Equal("2026-10-25T03:59:59+03:00", EnrollmentDomainRules.FormatInUniversityTimeZone(beforeAutumnBackUtc));
        Assert.Equal("2026-10-25T03:00:00+02:00", EnrollmentDomainRules.FormatInUniversityTimeZone(atAutumnBackUtc));

        // 3. Перевірка інваріантності межі вікна [opens_at, closes_at) при передачі моменту часу з різними часовими зміщеннями
        var opensAtUtc = DateTimeOffset.Parse("2026-09-01T07:00:00Z");   // 10:00:00+03:00 або 09:00:00+02:00
        var closesAtUtc = DateTimeOffset.Parse("2026-09-15T15:00:00Z");  // 18:00:00+03:00 або 17:00:00+02:00

        var sameOpenInOffsetPlus2 = DateTimeOffset.Parse("2026-09-01T09:00:00+02:00");
        var beforeOpenInOffsetPlus2 = DateTimeOffset.Parse("2026-09-01T08:59:59+02:00");
        var sameCloseInOffsetPlus3 = DateTimeOffset.Parse("2026-09-15T18:00:00+03:00");
        var sameCloseInOffsetMinus4 = DateTimeOffset.Parse("2026-09-15T11:00:00-04:00");

        Assert.False(EnrollmentDomainRules.IsWithinEnrollmentWindow(beforeOpenInOffsetPlus2, opensAtUtc, closesAtUtc));
        Assert.True(EnrollmentDomainRules.IsWithinEnrollmentWindow(sameOpenInOffsetPlus2, opensAtUtc, closesAtUtc));
        Assert.False(EnrollmentDomainRules.IsWithinEnrollmentWindow(sameCloseInOffsetPlus3, opensAtUtc, closesAtUtc));
        Assert.False(EnrollmentDomainRules.IsWithinEnrollmentWindow(sameCloseInOffsetMinus4, opensAtUtc, closesAtUtc));
    }

    // Спільна предметна політика (п. 13): Єдине правило перевірки запису для Web та РПО
    [Fact]
    public void EnrollmentPolicy_UnifiedCheck_CorrectlyValidatesAllConditions()
    {
        var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");
        var open = now.AddDays(-2);
        var close = now.AddDays(2);

        var validStudent = new StudentEntity("s-1", "s@test.com", "Test Student", 5, 2, 10.0m);
        var validGroup = new DisciplineGroupEntity("g-1", "d-1", "G1", 20, 5, open, close, "Europe/Kyiv", false, 5.0m);

        // 1. Успішний запис
        var okResult = EnrollmentPolicy.Check(validStudent, validGroup, 5.0m, now, alreadyEnrolledForDiscipline: false);
        Assert.True(okResult.Allowed);

        // 2. Закрита група
        var closedGroup = validGroup with { Closed = true };
        var closedResult = EnrollmentPolicy.Check(validStudent, closedGroup, 5.0m, now, false);
        Assert.False(closedResult.Allowed);
        Assert.Equal("group_closed", closedResult.ErrorCode);

        // 3. Поза вікном
        var windowResult = EnrollmentPolicy.Check(validStudent, validGroup, 5.0m, close.AddMinutes(1), false);
        Assert.False(windowResult.Allowed);
        Assert.Equal("enrollment_window_closed", windowResult.ErrorCode);

        // 4. Повторний запис на ту саму дисципліну
        var dupResult = EnrollmentPolicy.Check(validStudent, validGroup, 5.0m, now, alreadyEnrolledForDiscipline: true);
        Assert.False(dupResult.Allowed);
        Assert.Equal("duplicate_discipline_enrollment", dupResult.ErrorCode);

        // 5. Перевищення ліміту K дисциплін
        var maxCountStudent = validStudent with { ActiveCount = 5, MaxDisciplines = 5 };
        var countResult = EnrollmentPolicy.Check(maxCountStudent, validGroup, 5.0m, now, false);
        Assert.False(countResult.Allowed);
        Assert.Equal("discipline_limit_exceeded", countResult.ErrorCode);

        // 6. Перевищення 30 ЄКТС
        var maxEctsStudent = validStudent with { ActiveEcts = 26.0m };
        var ectsResult = EnrollmentPolicy.Check(maxEctsStudent, validGroup, 5.0m, now, false);
        Assert.False(ectsResult.Allowed);
        Assert.Equal("ects_limit_exceeded", ectsResult.ErrorCode);

        // 7. Відсутність вільних місць у групі
        var fullGroup = validGroup with { Available = 0 };
        var fullResult = EnrollmentPolicy.Check(validStudent, fullGroup, 5.0m, now, false);
        Assert.False(fullResult.Allowed);
        Assert.Equal("group_full", fullResult.ErrorCode);
    }
}
