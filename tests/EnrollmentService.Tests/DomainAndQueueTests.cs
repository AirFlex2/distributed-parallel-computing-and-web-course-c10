using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnrollmentService;
using EnrollmentService.Http;
using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Services;
using EnrollmentService.Services.Processing;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EnrollmentService.Tests;

public sealed class DomainAndQueueTests
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

    // Web О1.5: Кодування та декодування непрозорого курсора пагінації
    [Fact]
    public void CursorEncoding_RoundTripsAndRejectsCorruptedInput()
    {
        var ts = DateTimeOffset.Parse("2026-10-30T08:15:30.1234567Z");
        var id = Guid.Parse("8f14e45f-ceea-467a-9a38-1b241d45b001");

        string cursor = EnrollmentDomainRules.EncodeCursor(ts, id);
        Assert.True(EnrollmentDomainRules.TryDecodeCursor(cursor, out var decodedTs, out var decodedId));
        Assert.Equal(ts, decodedTs);
        Assert.Equal(id, decodedId);

        Assert.False(EnrollmentDomainRules.TryDecodeCursor("corrupted-cursor-xyz", out _, out _));
        Assert.False(EnrollmentDomainRules.TryDecodeCursor("", out _, out _));
    }

    // Web Д1.4: Розбір W3C traceparent
    [Fact]
    public void TryExtractTraceId_ValidatesW3cTraceparent()
    {
        Assert.True(EnrollmentDomainRules.TryExtractTraceId(
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            out string? traceId));
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", traceId);

        Assert.False(EnrollmentDomainRules.TryExtractTraceId(
            "00-00000000000000000000000000000000-00f067aa0ba902b7-01",
            out _));
        Assert.False(EnrollmentDomainRules.TryExtractTraceId("invalid-header", out _));
    }

    // РПО Д1.1 та Д1.3: Неблокуюча асинхронна черга MonitorRequestQueue (TaskCompletionSource + обмежена місткість)
    [Fact]
    public async Task MonitorRequestQueue_DequeueAsync_AwaitsNonBlockingAndDrainsOnComplete()
    {
        var queue = new MonitorRequestQueue(capacity: 2);

        // Запуск очікуючого споживача до появи елемента в черзі
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ValueTask<QueuedRequestItem?> pendingDequeue = queue.DequeueAsync(cts.Token);
        Assert.False(pendingDequeue.IsCompleted);

        var item1 = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var item2 = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-2",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var item3Overflow = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-3",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        Assert.True(queue.TryEnqueue(item1));
        var dequeued1 = await pendingDequeue;
        Assert.NotNull(dequeued1);
        Assert.Equal(item1.Id, dequeued1!.Id);

        // Перевірка межі місткості (capacity = 2)
        Assert.True(queue.TryEnqueue(item1));
        Assert.True(queue.TryEnqueue(item2));
        Assert.False(queue.TryEnqueue(item3Overflow));

        // Після Complete() нові елементи відхиляються, але вже прийняті вивантажуються до кінця
        queue.Complete();
        Assert.False(queue.TryEnqueue(item3Overflow));

        var d1 = await queue.DequeueAsync(cts.Token);
        var d2 = await queue.DequeueAsync(cts.Token);
        var dEnd = await queue.DequeueAsync(cts.Token);

        Assert.Equal(item1.Id, d1?.Id);
        Assert.Equal(item2.Id, d2?.Id);
        Assert.Null(dEnd);
    }

    // Web Д1.3: Одне правило RFC 9457 (Problem Details for HTTP APIs) як автоматизований тест
    // Перевіряє, що відповідь містить Content-Type application/problem+json,
    // поле 'status' чисельно дорівнює коду HTTP, а 'type' є валідним URI.
    [Theory]
    [InlineData(400, "invalid_json", "Синтаксична помилка JSON")]
    [InlineData(400, "invalid_request_id_format", "Некоректний формат ідентифікатора заявки")]
    [InlineData(404, "route_not_found", "Маршрут не знайдено")]
    [InlineData(404, "group_not_found", "Групу не знайдено")]
    [InlineData(405, "method_not_allowed", "HTTP-метод не підтримується")]
    [InlineData(422, "ects_limit_exceeded", "Перевищено ліміт ЄКТС")]
    [InlineData(422, "missing_required_fields", "Помилка валідації полів")]
    [InlineData(503, "service_shutting_down", "Сервіс завершує роботу")]
    public void Web_D13_Rfc9457_ProblemDetails_ContractCompliance(int statusCode, string errorCode, string title)
    {
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.Path = "/v1/test-endpoint";
        httpContext.Items["RequestId"] = "req-test-123";

        var opts = new ServiceOptions { InstanceId = "app-test" };
        var result = EnrollmentService.Http.ProblemDetailsMapper.CreateProblemResult(
            httpContext, opts, statusCode, title, "Тестовий опис проблеми згідно з RFC 9457.", errorCode);

        var problemHttpResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IContentTypeHttpResult>(result);
        Assert.Equal("application/problem+json", problemHttpResult.ContentType);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(statusCode, statusCodeResult.StatusCode);

        var valueResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(result);
        var problem = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ProblemDetails>(valueResult.Value);

        Assert.Equal(statusCode, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Equal("Тестовий опис проблеми згідно з RFC 9457.", problem.Detail);
        Assert.StartsWith("https://urn.c10.edu.ua/problems/", problem.Type);
        Assert.Equal("/v1/test-endpoint", problem.Instance);
    }

    // Спільна предметна політика (п. 13): Єдине правило перевірки запису для Web та РПО
    [Fact]
    public void EnrollmentPolicy_UnifiedCheck_CorrectlyValidatesAllConditions()
    {
        var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");
        var open = now.AddDays(-2);
        var close = now.AddDays(2);

        var validStudent = new EnrollmentService.Domain.StudentEntity("s-1", "s@test.com", "Test Student", 5, 2, 10.0m);
        var validGroup = new EnrollmentService.Domain.DisciplineGroupEntity("g-1", "d-1", "G1", 20, 5, open, close, "Europe/Kyiv", false, 5.0m);

        // 1. Успішний запис
        var okResult = EnrollmentService.Domain.EnrollmentPolicy.Check(validStudent, validGroup, 5.0m, now, alreadyEnrolledForDiscipline: false);
        Assert.True(okResult.Allowed);

        // 2. Закрита група
        var closedGroup = validGroup with { Closed = true };
        var closedResult = EnrollmentService.Domain.EnrollmentPolicy.Check(validStudent, closedGroup, 5.0m, now, false);
        Assert.False(closedResult.Allowed);
        Assert.Equal("group_closed", closedResult.ErrorCode);

        // 3. Поза вікном
        var windowResult = EnrollmentService.Domain.EnrollmentPolicy.Check(validStudent, validGroup, 5.0m, close.AddMinutes(1), false);
        Assert.False(windowResult.Allowed);
        Assert.Equal("enrollment_window_closed", windowResult.ErrorCode);

        // 4. Повторний запис на ту саму дисципліну
        var dupResult = EnrollmentService.Domain.EnrollmentPolicy.Check(validStudent, validGroup, 5.0m, now, alreadyEnrolledForDiscipline: true);
        Assert.False(dupResult.Allowed);
        Assert.Equal("duplicate_discipline_enrollment", dupResult.ErrorCode);

        // 5. Перевищення ліміту K дисциплін
        var maxCountStudent = validStudent with { ActiveCount = 5, MaxDisciplines = 5 };
        var countResult = EnrollmentService.Domain.EnrollmentPolicy.Check(maxCountStudent, validGroup, 5.0m, now, false);
        Assert.False(countResult.Allowed);
        Assert.Equal("discipline_limit_exceeded", countResult.ErrorCode);

        // 6. Перевищення 30 ЄКТС
        var maxEctsStudent = validStudent with { ActiveEcts = 26.0m };
        var ectsResult = EnrollmentService.Domain.EnrollmentPolicy.Check(maxEctsStudent, validGroup, 5.0m, now, false);
        Assert.False(ectsResult.Allowed);
        Assert.Equal("ects_limit_exceeded", ectsResult.ErrorCode);

        // 7. Відсутність вільних місць у групі
        var fullGroup = validGroup with { Available = 0 };
        var fullResult = EnrollmentService.Domain.EnrollmentPolicy.Check(validStudent, fullGroup, 5.0m, now, false);
        Assert.False(fullResult.Allowed);
        Assert.Equal("group_full", fullResult.ErrorCode);
    }

    // РПО: Перевірка життєвого циклу резервування слотів у чергах (MonitorRequestQueue та ChannelRequestQueue)
    [Fact]
    public void RequestQueue_SlotReservationLifecycle_TracksAvailableCapacityCorrectly()
    {
        var monitorQueue = new MonitorRequestQueue(capacity: 2);
        Assert.True(monitorQueue.TryReserveSlot());
        Assert.True(monitorQueue.TryReserveSlot());
        Assert.False(monitorQueue.TryReserveSlot()); // capacity exhausted

        // Звільнення слота при помилці/відмові
        monitorQueue.ReleaseSlot();
        Assert.True(monitorQueue.TryReserveSlot());

        var item = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-1",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        // Постановка у зарезервований слот
        Assert.True(monitorQueue.EnqueueReserved(item));
        Assert.Equal(1, monitorQueue.Count);

        var channelQueue = new ChannelRequestQueue(capacity: 2);
        Assert.True(channelQueue.TryReserveSlot());
        Assert.True(channelQueue.TryReserveSlot());
        Assert.False(channelQueue.TryReserveSlot());

        channelQueue.ReleaseSlot();
        Assert.True(channelQueue.TryReserveSlot());
        Assert.True(channelQueue.EnqueueReserved(item));
        Assert.Equal(1, channelQueue.Count);
    }

    // РПО Д1.2: Перевірка накопичення метрик для закону Літтла без втрати історії
    [Fact]
    public void MetricsCollector_LittlesLawAccumulators_MaintainsCorrectSumsAndAverages()
    {
        var metrics = new EnrollmentService.Metrics.MetricsCollector();
        var opts = new ServiceOptions { QueueCapacity = 10, Workers = 2 };

        metrics.ResetMeasurementWindow(currentQueueLength: 0);

        // Імітація періодичних незалежних вибірок (більше 500 вибірок)
        // 600 вибірок по (0, 1, 2, 3, 4) повторюються 120 разів. Сума = 120 * 10 = 1200, середнє = 2.0, max = 4.
        for (int i = 0; i < 600; i++)
        {
            metrics.RecordIndependentPeriodicSample(i % 5);
        }

        object snapshotObj = metrics.BuildMetricsSnapshot(0, opts, false);
        Assert.NotNull(snapshotObj);

        var json = System.Text.Json.JsonSerializer.Serialize(snapshotObj);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var littlesLaw = doc.RootElement.GetProperty("littles_law");

        Assert.Equal(600, littlesLaw.GetProperty("independent_sample_count").GetInt32());
        Assert.Equal(2.0, littlesLaw.GetProperty("l_observed_independent_sampled").GetDouble());
        Assert.Equal(4, littlesLaw.GetProperty("max_queue_length_observed").GetInt32());
    }

    // РПО Д1.1 та Д1.3: Перевірка ChannelRequestQueue з кількома паралельними воркерами (перевірка while на WaitToReadAsync)
    [Fact]
    public async Task ChannelRequestQueue_MultipleWorkers_ProcessesAllItemsWithoutPrematureExit()
    {
        var queue = new ChannelRequestQueue(capacity: 50);
        int workerCount = 4;
        int totalItems = 30;
        var processed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (true)
            {
                var item = await queue.DequeueAsync(cts.Token);
                if (item == null) break;
                processed.Add(item.Id);
                await Task.Yield();
            }
        }).ToArray();

        for (int i = 0; i < totalItems; i++)
        {
            var item = new QueuedRequestItem
            {
                Id = Guid.NewGuid(),
                StudentId = $"student-{i}",
                GroupId = "DISC-DPC-01",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            Assert.True(queue.TryEnqueue(item));
            if (i % 5 == 0) await Task.Delay(5);
        }

        queue.Complete();
        await Task.WhenAll(workers);

        Assert.Equal(totalItems, processed.Count);
    }

    // О1.5: Перевірка коректності вимірювання delivery_wait_ms при ранньому опитуванні (polling) ДО завершення обробки
    [Fact]
    public void MetricsCollector_FirstReadBeforeWorkerFinished_CorrectlyRecordsDeliverySample()
    {
        var metrics = new EnrollmentService.Metrics.MetricsCollector();
        var opts = new ServiceOptions { QueueCapacity = 10, Workers = 2 };
        var id = Guid.NewGuid();

        var item = new QueuedRequestItem
        {
            Id = id,
            StudentId = "student-1",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        metrics.RecordAccepted(item, currentQueueLength: 1);
        metrics.RecordWorkerStarted(item, currentQueueLength: 0);

        // 1. Клієнт виконує ранній опрос (polling) через GET ДО завершення заявки воркером
        metrics.RecordFirstReadIfCompleted(id);

        // Перевіряємо, що ранній опрос НЕ встановив delivery_wait_ms (він залишається null)
        var (earlyQWait, earlyProc, earlyDeliv) = metrics.GetItemTimings(id);
        Assert.NotNull(earlyQWait);
        Assert.Null(earlyProc);
        Assert.Null(earlyDeliv);

        // 2. Воркер встановлює відмітку завершення
        metrics.MarkProcessingFinished(item);
        metrics.RecordWorkerFinished(item, "done", currentQueueLength: 0);

        // 3. Клієнт отримує вже завершену заявку через GET (done)
        metrics.RecordFirstReadIfCompleted(id);

        // Перевіряємо, що таймінги зафіксовані коректно, delivery не null і >= 0
        var (qWait, proc, deliv) = metrics.GetItemTimings(id);
        Assert.NotNull(qWait);
        Assert.NotNull(proc);
        Assert.NotNull(deliv);
        Assert.True(deliv >= 0.0);

        // Перевіряємо, що в загальному знімку метрик delivery_wait_ms має sample_count = 1
        var snapshot = metrics.BuildMetricsSnapshot(0, opts, false);
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var deliveryWait = doc.RootElement.GetProperty("intervals_ms").GetProperty("delivery_wait_ms");
        Assert.Equal(1, deliveryWait.GetProperty("sample_count").GetInt32());
    }

    // Д1.4: Перевірка обов'язкового Bearer-токена для /metrics та /v1/groups/{id}/close
    [Fact]
    public async Task BearerAuthMiddleware_EnforcesTokenOnMetricsAndGroupClose()
    {
        var opts = new ServiceOptions { ApiAuthToken = "test-token-123" };
        var nextCalled = false;
        RequestDelegate next = (ctx) => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new BearerAuthMiddleware(next, opts);

        // 1. /metrics без токена -> 401 Unauthorized
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Path = "/metrics";
        ctx1.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(ctx1);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx1.Response.StatusCode);

        // 2. /metrics з недійсним токеном -> 401 Unauthorized
        nextCalled = false;
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Path = "/metrics";
        ctx2.Request.Headers.Authorization = "Bearer wrong-token";
        ctx2.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(ctx2);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx2.Response.StatusCode);

        // 3. /metrics з валідним токеном -> доступ дозволено (виклик наступного middleware)
        nextCalled = false;
        var ctx3 = new DefaultHttpContext();
        ctx3.Request.Path = "/metrics";
        ctx3.Request.Headers.Authorization = "Bearer test-token-123";
        await middleware.InvokeAsync(ctx3);
        Assert.True(nextCalled);

        // 4. /v1/groups/DISC-01/close без токена -> 401 Unauthorized
        nextCalled = false;
        var ctx4 = new DefaultHttpContext();
        ctx4.Request.Path = "/v1/groups/DISC-01/close";
        ctx4.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(ctx4);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx4.Response.StatusCode);

        // 5. /health без токена -> доступ дозволено без автентифікації
        nextCalled = false;
        var ctx5 = new DefaultHttpContext();
        ctx5.Request.Path = "/health";
        await middleware.InvokeAsync(ctx5);
        Assert.True(nextCalled);
    }

    // РПО Д1.4: Тестування сценарію Graceful Shutdown (SIGTERM) при повністю заповненій черзі:
    // 1) нові запити відхиляються з кодом 503 Service Unavailable (service_shutting_down),
    // 2) активний пул робочих потоків (EnrollmentWorkerPool) безперешкодно дообробляє всі раніше прийняті заявки
    //    до кінцевого стану (done),
    // 3) черга повністю спустошується, воркери безпечно зупиняються без витоків, помилок або втрати даних.
    [Fact]
    public async Task Dpc_D14_GracefulShutdown_FullQueue_RejectsNewRequestsWith503_AndDrainsAllQueuedRequests()
    {
        const int capacity = 3;
        var queue = new MonitorRequestQueue(capacity);
        var options = new ServiceOptions
        {
            InstanceId = "test-graceful-node",
            ExecutionModel = "async_tasks",
            QueueCapacity = capacity,
            EnableWorkers = true,
            Workers = 2
        };
        var metrics = new MetricsCollector();
        var lifetime = new TestHostLifetime();
        var testProcessor = new TestShutdownProcessor();
        var pool = new EnrollmentWorkerPool(queue, testProcessor, null, options, metrics, lifetime);

        // 1. Заповнюємо чергу до максимальної місткості
        var queuedItems = new List<QueuedRequestItem>();
        for (int i = 1; i <= capacity; i++)
        {
            var item = new QueuedRequestItem
            {
                Id = Guid.NewGuid(),
                StudentId = $"student-{i}",
                GroupId = "DISC-DPC-01",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            queuedItems.Add(item);
            bool enqueued = queue.TryEnqueue(item);
            Assert.True(enqueued, $"Item {i} should be successfully enqueued.");
        }
        Assert.Equal(capacity, queue.Count);

        // 2. Запускаємо реальний пул воркерів
        await pool.StartAsync(CancellationToken.None);

        // 3. Ініціюємо Graceful Shutdown (імітація сигналу SIGTERM Д1.4)
        pool.InitiateShutdown();
        Assert.True(pool.IsShuttingDown);

        // 4. Перевіряємо роботу GracefulShutdownMiddleware:
        // Змінюючий POST запит повинен отримати HTTP 503 "service_shutting_down"
        bool nextCalled = false;
        var middleware = new GracefulShutdownMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            options,
            pool);

        var postContext = new DefaultHttpContext();
        postContext.Request.Method = HttpMethods.Post;
        postContext.Request.Path = "/v1/enrollment-requests";
        postContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(postContext);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, postContext.Response.StatusCode);

        // Перевіряємо, що черга більше не приймає нових елементів після завершення
        var overflowItem = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "overflow-student",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        Assert.False(queue.TryEnqueue(overflowItem));

        // 5. Перевіряємо, що читаючий GET запит (перевірка стану) не блокується middleware
        nextCalled = false;
        var getContext = new DefaultHttpContext();
        getContext.Request.Method = HttpMethods.Get;
        getContext.Request.Path = "/v1/enrollment-requests/" + queuedItems[0].Id;
        await middleware.InvokeAsync(getContext);
        Assert.True(nextCalled);

        // 6. Чекаємо коректного завершення роботи воркерів (StopAsync виконує повне вивантаження черги)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pool.StopAsync(cts.Token);

        // 7. Перевіряємо, що всі заявки були реально оброблені пулом воркерів і переведені у статус done
        Assert.Equal(capacity, testProcessor.ProcessedStatus.Count);
        foreach (var item in queuedItems)
        {
            Assert.True(testProcessor.ProcessedStatus.TryGetValue(item.Id, out var status));
            Assert.Equal("done", status);
        }

        // 8. Черга повністю порожня, застосунок сигналізував про завершення
        Assert.Equal(0, queue.Count);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task WebEnrollmentNotifier_FinalAttemptException_ReturnsFalseWithoutThrowing()
    {
        var mockService = new ThrowingConfirmationService(new InvalidOperationException("Fatal DB error on attempt"));
        var notifier = new WebEnrollmentNotifier(mockService);

        bool result = await notifier.NotifyRosterRecordedAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(3, mockService.CallCount);
    }

    [Fact]
    public async Task WebEnrollmentNotifier_CancellationRequested_RethrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockService = new ThrowingConfirmationService(new OperationCanceledException(cts.Token));
        var notifier = new WebEnrollmentNotifier(mockService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await notifier.NotifyRosterRecordedAsync(Guid.NewGuid(), Guid.NewGuid(), cts.Token);
        });
    }

    [Fact]
    public async Task WebEnrollmentNotifier_PermanentFailure_ReturnsFalseImmediatelyWithoutRetrying()
    {
        var stubService = new StubConfirmationService(RosterConfirmationResult.PermanentFailure);
        var notifier = new WebEnrollmentNotifier(stubService);

        bool result = await notifier.NotifyRosterRecordedAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, stubService.CallCount);
    }

    [Fact]
    public async Task MetricsCollector_RecoveredItem_QueueWaitMsFixedAtWorkerStart_DoesNotGrowWithUtcNow()
    {
        var collector = new MetricsCollector();
        var item = new QueuedRequestItem
        {
            Id = Guid.NewGuid(),
            StudentId = "student-rec-test",
            GroupId = "DISC-DPC-01",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(-500),
            IsRecoveredFromDb = true
        };

        // Записуємо взяття заявки воркером
        collector.RecordWorkerStarted(item, 0);

        var (qWait1, _, _) = collector.GetItemTimings(item.Id);
        Assert.NotNull(qWait1);
        Assert.True(qWait1.Value >= 400.0);

        // Чекаємо деякий час
        await Task.Delay(60);

        var (qWait2, _, _) = collector.GetItemTimings(item.Id);
        Assert.NotNull(qWait2);

        // Час очікування повинен залишатися фіксованим і НЕ зростати від UtcNow
        Assert.Equal(qWait1.Value, qWait2.Value);
    }

    private sealed class ThrowingConfirmationService : IWebEnrollmentConfirmationService
    {
        private readonly Exception _exceptionToThrow;
        public int CallCount { get; private set; }

        public ThrowingConfirmationService(Exception exceptionToThrow)
        {
            _exceptionToThrow = exceptionToThrow;
        }

        public Task<RosterConfirmationResult> ConfirmEnrollmentDetailedAsync(
            Guid enrollmentId,
            Guid requestId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw _exceptionToThrow;
        }
    }

    private sealed class StubConfirmationService : IWebEnrollmentConfirmationService
    {
        private readonly RosterConfirmationResult _result;
        public int CallCount { get; private set; }

        public StubConfirmationService(RosterConfirmationResult result)
        {
            _result = result;
        }

        public Task<RosterConfirmationResult> ConfirmEnrollmentDetailedAsync(
            Guid enrollmentId,
            Guid requestId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task EnrollmentWorkerPool_WhenEnableWorkersFalse_ShutdownDoesNotDrainAndSignalsStopImmediately()
    {
        var queue = new MonitorRequestQueue(capacity: 10);
        var metrics = new MetricsCollector();
        var options = new ServiceOptions
        {
            InstanceId = "app-b",
            EnableWorkers = false,
            Workers = 3,
            QueueCapacity = 10
        };
        var lifetime = new TestHostLifetime();
        var pool = new EnrollmentWorkerPool(queue, null, null, options, metrics, lifetime);

        await pool.StartAsync(CancellationToken.None);

        pool.InitiateShutdown();
        Assert.True(pool.IsShuttingDown);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await pool.StopAsync(stopCts.Token);
        Assert.Equal(0, queue.Count);
    }

    private sealed class TestShutdownProcessor : IEnrollmentRequestProcessor
    {
        public ConcurrentDictionary<Guid, string> ProcessedStatus { get; } = new();

        public async Task ProcessItemAsync(
            int workerId,
            QueuedRequestItem item,
            bool isAsyncModel,
            IBoundedRequestQueue queue,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            ProcessedStatus[item.Id] = "done";
        }
    }

    private sealed class TestHostLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;
        public void StopApplication() => _stoppingCts.Cancel();
    }
}
