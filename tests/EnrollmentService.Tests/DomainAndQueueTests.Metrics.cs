using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using EnrollmentService;
using EnrollmentService.Domain;
using EnrollmentService.Http;
using EnrollmentService.Metrics;
using EnrollmentService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EnrollmentService.Tests;

public sealed partial class DomainAndQueueTests
{
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
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/v1/test-endpoint";
        httpContext.Items["RequestId"] = "req-test-123";

        var opts = new ServiceOptions { InstanceId = "app-test" };
        var result = ProblemDetailsMapper.CreateProblemResult(
            httpContext, opts, statusCode, title, "Тестовий опис проблеми згідно з RFC 9457.", errorCode);

        var problemHttpResult = Assert.IsAssignableFrom<IContentTypeHttpResult>(result);
        Assert.Equal("application/problem+json", problemHttpResult.ContentType);

        var statusCodeResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(statusCode, statusCodeResult.StatusCode);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(valueResult.Value);

        Assert.Equal(statusCode, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Equal("Тестовий опис проблеми згідно з RFC 9457.", problem.Detail);
        Assert.StartsWith("https://urn.c10.edu.ua/problems/", problem.Type);
        Assert.Equal("/v1/test-endpoint", problem.Instance);
    }

    // РПО Д1.2: Перевірка накопичення метрик для закону Літтла без втрати історії
    [Fact]
    public void MetricsCollector_LittlesLawAccumulators_MaintainsCorrectSumsAndAverages()
    {
        var metrics = new MetricsCollector();
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

        var json = JsonSerializer.Serialize(snapshotObj);
        using var doc = JsonDocument.Parse(json);
        var littlesLaw = doc.RootElement.GetProperty("littles_law");

        Assert.Equal(600, littlesLaw.GetProperty("independent_sample_count").GetInt32());
        Assert.Equal(2.0, littlesLaw.GetProperty("l_observed_independent_sampled").GetDouble());
        Assert.Equal(4, littlesLaw.GetProperty("max_queue_length_observed").GetInt32());
    }

    // О1.5: Перевірка коректності вимірювання delivery_wait_ms при ранньому опитуванні (polling) ДО завершення обробки
    [Fact]
    public void MetricsCollector_FirstReadBeforeWorkerFinished_CorrectlyRecordsDeliverySample()
    {
        var metrics = new MetricsCollector();
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
        var json = JsonSerializer.Serialize(snapshot);
        using var doc = JsonDocument.Parse(json);
        var deliveryWait = doc.RootElement.GetProperty("intervals_ms").GetProperty("delivery_wait_ms");
        Assert.Equal(1, deliveryWait.GetProperty("sample_count").GetInt32());
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
}
