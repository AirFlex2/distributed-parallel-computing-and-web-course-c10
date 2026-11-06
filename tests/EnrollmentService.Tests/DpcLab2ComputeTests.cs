using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EnrollmentService.Services.Compute;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EnrollmentService.Tests;

public sealed class DpcLab2ComputeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DpcLab2ComputeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DB_CONNECTION_STRING", TestEnvironment.ConnectionString);
            builder.UseSetting("API_AUTH_TOKEN", TestEnvironment.ApiAuthToken);
        });
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestEnvironment.ApiAuthToken);
        return client;
    }

    [Fact]
    public void C10IntensityMath_AnalyticalIntegral_MatchesAntiderivativeDifference()
    {
        double modelA = 50.0;
        double modelB = 5.0;
        double modelTau = 10.0;

        double a = 0.0;
        double b = 30.0;

        double fA = C10IntensityMath.EvaluateAntiderivative(a, modelA, modelB, modelTau);
        double fB = C10IntensityMath.EvaluateAntiderivative(b, modelA, modelB, modelTau);
        double expected = fB - fA;
        double actual = C10IntensityMath.ComputeAnalyticalIntegral(a, b, modelA, modelB, modelTau);

        Assert.Equal(expected, actual, precision: 12);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(10, 2)]
    [InlineData(10, 4)]
    [InlineData(100003, 1)]
    [InlineData(100003, 2)]
    [InlineData(100003, 4)]
    public void C10IntensityMath_GetWorkerSubintervalBounds_CoversExactRangeWithoutGapsOrOverlaps(long n, int workers)
    {
        long expectedStart = 0;
        long totalCovered = 0;

        for (int w = 0; w < workers; w++)
        {
            var (start, end) = C10IntensityMath.GetWorkerSubintervalBounds(n, workers, w);
            Assert.Equal(expectedStart, start);
            Assert.True(end >= start);
            totalCovered += (end - start);
            expectedStart = end;
        }

        Assert.Equal(n, expectedStart);
        Assert.Equal(n, totalCovered);
    }

    [Fact]
    public void C10IntensityMath_AccuracyOrder_ErrorDecreasesFourfoldWhenDoublingN()
    {
        double a = 0.0;
        double b = 20.0;
        double modelA = 50.0;
        double modelB = 5.0;
        double modelTau = 10.0;

        double reference = C10IntensityMath.ComputeAnalyticalIntegral(a, b, modelA, modelB, modelTau);

        long n1 = 1_000;
        long n2 = 2_000;
        long n4 = 4_000;

        double val1 = C10IntensityMath.ComputeTrapezoidSliceLocal(a, b, n1, 0, n1, modelA, modelB, modelTau);
        double val2 = C10IntensityMath.ComputeTrapezoidSliceLocal(a, b, n2, 0, n2, modelA, modelB, modelTau);
        double val4 = C10IntensityMath.ComputeTrapezoidSliceLocal(a, b, n4, 0, n4, modelA, modelB, modelTau);

        double err1 = Math.Abs(val1 - reference);
        double err2 = Math.Abs(val2 - reference);
        double err4 = Math.Abs(val4 - reference);

        double ratio12 = err1 / err2;
        double ratio24 = err2 / err4;

        // Для складеної формули трапецій O(h^2) подвоєння n зменшує похибку у ~4 рази
        Assert.InRange(ratio12, 3.95, 4.05);
        Assert.InRange(ratio24, 3.98, 4.02);
    }

    [Fact]
    public void C10IntensityMath_OptimizationHypothesis_CompositeMatchesNaivePerSubinterval()
    {
        double a = 0.0;
        double b = 15.0;
        long n = 50_000;
        double modelA = 50.0;
        double modelB = 5.0;
        double modelTau = 10.0;

        double compositeVal = C10IntensityMath.ComputeTrapezoidSliceLocal(a, b, n, 0, n, modelA, modelB, modelTau);
        double naiveVal = C10IntensityMath.ComputeTrapezoidSliceNaivePerSubinterval(a, b, n, 0, n, modelA, modelB, modelTau);

        Assert.True(Math.Abs(compositeVal - naiveVal) < 1e-11);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Dpc_O21_PostCompute_LocalMode_MatchesAnalyticalReferenceAcrossWorkerCounts(int workers)
    {
        Skip.If(!await TestEnvironment.IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        using var client = CreateAuthorizedClient();
        var payload = new
        {
            a = 0.0,
            b = 10.0,
            n = 200_000,
            workers,
            method = "trapezoid",
            order = "natural",
            mode = "local"
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/compute", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double value = root.GetProperty("value").GetDouble();
        double reference = root.GetProperty("reference").GetDouble();
        double absError = root.GetProperty("abs_error").GetDouble();
        double relError = root.GetProperty("rel_error").GetDouble();
        double wallS = root.GetProperty("wall_s").GetDouble();
        int respWorkers = root.GetProperty("workers").GetInt32();
        string respMode = root.GetProperty("mode").GetString()!;

        Assert.Equal(workers, respWorkers);
        Assert.Equal("local", respMode);
        Assert.True(wallS > 0.0);
        Assert.True(absError <= 1.0e-6, $"Очікувалася абсолютна похибка <= 1e-6, отримано {absError}");
        Assert.True(relError <= 1.0e-6);
        Assert.Equal(reference, value, precision: 6);
    }

    [SkippableFact]
    public async Task Dpc_O22_PostCompute_EdgeCases_ZeroWidthAndSingleSubinterval()
    {
        Skip.If(!await TestEnvironment.IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        using var client = CreateAuthorizedClient();

        // 1. Вузький відрізок a == b -> інтеграл 0.0
        var zeroPayload = new
        {
            a = 5.0,
            b = 5.0,
            n = 100,
            workers = 2,
            method = "trapezoid",
            order = "natural",
            mode = "local"
        };

        using var zeroRes = await client.PostAsync(
            "/v1/compute",
            new StringContent(JsonSerializer.Serialize(zeroPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, zeroRes.StatusCode);
        using var zeroDoc = JsonDocument.Parse(await zeroRes.Content.ReadAsStringAsync());
        Assert.Equal(0.0, zeroDoc.RootElement.GetProperty("value").GetDouble(), precision: 12);
        Assert.Equal(0.0, zeroDoc.RootElement.GetProperty("reference").GetDouble(), precision: 12);
        Assert.Equal(0.0, zeroDoc.RootElement.GetProperty("abs_error").GetDouble(), precision: 12);

        // 2. Мінімальне розбиття n = 1 на 4 процесах (3 процеси отримують порожній діапазон [start == end], 1 процес отримує [0, 1])
        var n1Payload = new
        {
            a = 0.0,
            b = 1.0,
            n = 1,
            workers = 4,
            method = "trapezoid",
            order = "natural",
            mode = "local"
        };

        using var n1Res = await client.PostAsync(
            "/v1/compute",
            new StringContent(JsonSerializer.Serialize(n1Payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, n1Res.StatusCode);
        using var n1Doc = JsonDocument.Parse(await n1Res.Content.ReadAsStringAsync());
        double expectedSingleTrapezoid = 0.5 * (
            C10IntensityMath.EvaluateLambda(0.0, 50.0, 5.0, 10.0) +
            C10IntensityMath.EvaluateLambda(1.0, 50.0, 5.0, 10.0));
        Assert.Equal(expectedSingleTrapezoid, n1Doc.RootElement.GetProperty("value").GetDouble(), precision: 10);
    }

    [SkippableTheory]
    [InlineData("lock")]
    [InlineData("none")]
    public async Task Dpc_O24_PostCompute_SmallN1With4Workers_SharedMemoryModes_CompleteWithoutBarrierTimeout(string mode)
    {
        Skip.If(!await TestEnvironment.IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        using var client = CreateAuthorizedClient();

        var payload = new
        {
            a = 0.0,
            b = 1.0,
            n = 1,
            workers = 4,
            method = "trapezoid",
            order = "natural",
            mode
        };

        using var res = await client.PostAsync(
            "/v1/compute",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        double expectedSingleTrapezoid = 0.5 * (
            C10IntensityMath.EvaluateLambda(0.0, 50.0, 5.0, 10.0) +
            C10IntensityMath.EvaluateLambda(1.0, 50.0, 5.0, 10.0));

        Assert.Equal(expectedSingleTrapezoid, root.GetProperty("value").GetDouble(), precision: 10);
        // Переконуємося, що бар'єр не чекає 2500 мс тайм-ауту через порожні діапазони у 3 з 4 процесів
        double wallS = root.GetProperty("wall_s").GetDouble();
        Assert.True(wallS < 2.0, $"Очікувався швидкий вихід із бар'єра (wall_s < 2.0 с), але отримано wall_s={wallS:F3} с у режимі mode={mode}");

        // Додаткова перевірка повноцінної синхронізації всіх workers=4 процесів на бар'єрі при n=2:
        // запускаються реальні окремі дочірні процеси ОС через ProcessStartInfo.
        // Два процеси з порожнім діапазоном [2, 2) стартують першими.
        // За допомогою спільної пам'яті тест детерміновано очікує, поки обидва порожні процеси
        // реально увійдуть у бар'єр і збільшать readyCount до 2 (без сліпої фіксованої затримки).
        // Після цього перевіряється, що вони НЕ завершилися (HasExited == false),
        // і лише після запуску двох обчислювальних процесів [0, 1) та [1, 2) всі 4 процеси завершуються з кодом 0.
        string shmPath = Path.Combine(Path.GetTempPath(), $"c10_test_barrier_{Guid.NewGuid():N}.bin");
        Process? proc2 = null;
        Process? proc3 = null;
        Process? proc0 = null;
        Process? proc1 = null;

        try
        {
            await using (var initFs = new FileStream(
                shmPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] zeros = new byte[ComputeWorkerProcess.SharedMemoryByteSize];
                await initFs.WriteAsync(zeros);
                await initFs.FlushAsync();
            }

            int ReadReadyCount()
            {
                using var fs = new FileStream(shmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> buf = stackalloc byte[16];
                fs.ReadExactly(buf);
                return BitConverter.ToInt32(buf.Slice(12, 4));
            }

            ProcessStartInfo BuildPsi(long start, long end) =>
                ParallelComputeService.CreateWorkerStartInfo(
                    a: 0.0,
                    b: 2.0,
                    n: 2,
                    startIdx: start,
                    endIdx: end,
                    workers: 4,
                    modelA: 50.0,
                    modelB: 5.0,
                    modelTau: 10.0,
                    mode: mode,
                    kernel: "composite",
                    sharedFilePath: shmPath,
                    barrierTimeoutMs: 5000);

            proc2 = Process.Start(BuildPsi(2, 2))!;
            proc3 = Process.Start(BuildPsi(2, 2))!;

            // Детерміновано очікуємо, поки обидва порожні процеси реально увійдуть у бар'єр і збільшать readyCount до 2
            var swWaitReady = System.Diagnostics.Stopwatch.StartNew();
            while (swWaitReady.ElapsedMilliseconds < 5000)
            {
                if (ReadReadyCount() >= 2)
                {
                    break;
                }
                await Task.Delay(10);
            }

            int readyCountBeforeActive = ReadReadyCount();
            Assert.Equal(2, readyCountBeforeActive);
            Assert.False(proc2.HasExited, "Дочірній процес ОС із порожнім діапазоном не повинен виходити з бар'єра, поки readyCount (2) < workers (4)");
            Assert.False(proc3.HasExited, "Дочірній процес ОС із порожнім діапазоном не повинен виходити з бар'єра, поки readyCount (2) < workers (4)");

            // Запускаємо два обчислювальні процеси
            proc0 = Process.Start(BuildPsi(0, 1))!;
            proc1 = Process.Start(BuildPsi(1, 2))!;

            await Task.WhenAll(
                proc2.WaitForExitAsync(),
                proc3.WaitForExitAsync(),
                proc0.WaitForExitAsync(),
                proc1.WaitForExitAsync());

            Assert.Equal(0, proc2.ExitCode);
            Assert.Equal(0, proc3.ExitCode);
            Assert.Equal(0, proc0.ExitCode);
            Assert.Equal(0, proc1.ExitCode);

            int finalReadyCount = ReadReadyCount();
            Assert.Equal(4, finalReadyCount);
        }
        finally
        {
            if (proc2 != null) { try { if (!proc2.HasExited) proc2.Kill(true); } catch { } proc2.Dispose(); }
            if (proc3 != null) { try { if (!proc3.HasExited) proc3.Kill(true); } catch { } proc3.Dispose(); }
            if (proc0 != null) { try { if (!proc0.HasExited) proc0.Kill(true); } catch { } proc0.Dispose(); }
            if (proc1 != null) { try { if (!proc1.HasExited) proc1.Kill(true); } catch { } proc1.Dispose(); }

            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }
        }
    }

    [Fact]
    public async Task Dpc_O24_ComputeWorker_BarrierTimeout_FailsWithErrorCodeAndDoesNotProceed()
    {
        // Перевіряємо обов'язкову валідацію додатності barrierTimeoutMs (> 0)
        Assert.Throws<ArgumentOutOfRangeException>(() => ParallelComputeService.CreateWorkerStartInfo(
            a: 0.0, b: 10.0, n: 100, startIdx: 0, endIdx: 25, workers: 4,
            modelA: 50.0, modelB: 5.0, modelTau: 10.0, mode: "lock", kernel: "composite",
            sharedFilePath: "test.bin", barrierTimeoutMs: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => ParallelComputeService.CreateWorkerStartInfo(
            a: 0.0, b: 10.0, n: 100, startIdx: 0, endIdx: 25, workers: 4,
            modelA: 50.0, modelB: 5.0, modelTau: 10.0, mode: "lock", kernel: "composite",
            sharedFilePath: "test.bin", barrierTimeoutMs: -100));

        string shmPath = Path.Combine(Path.GetTempPath(), $"c10_test_barrier_timeout_{Guid.NewGuid():N}.bin");
        Process? proc = null;
        try
        {
            await using (var fs = new FileStream(shmPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] zeros = new byte[ComputeWorkerProcess.SharedMemoryByteSize];
                await fs.WriteAsync(zeros);
            }

            // Запускаємо лише 1 процес із workers=4 і коротким бар'єрним тайм-аутом 300 мс (інші 3 процеси не запускаються)
            var psi = ParallelComputeService.CreateWorkerStartInfo(
                a: 0.0,
                b: 10.0,
                n: 100,
                startIdx: 0,
                endIdx: 25,
                workers: 4,
                modelA: 50.0,
                modelB: 5.0,
                modelTau: 10.0,
                mode: "lock",
                kernel: "composite",
                sharedFilePath: shmPath,
                barrierTimeoutMs: 300);

            proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // Перевіряємо, що при тайм-ауті процес НЕ продовжує вирахування, а негайно завершується з помилкою (exit code 1)
            Assert.Equal(1, proc.ExitCode);
            Assert.True(stderr.Contains("Синхронізаційний бар'єр") || stderr.Contains("бар'єр") || stderr.Contains("TimeoutException"),
                $"Expected barrier timeout error in stderr, but got: {stderr}");
            Assert.DoesNotContain("0\n", stdout);
        }
        finally
        {
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                proc.Dispose();
            }
            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }
        }
    }

    [SkippableFact]
    public async Task Dpc_O24_PostCompute_ModesComparison_NoneLosesUpdates_LockAndLocalAreExact()
    {
        Skip.If(!await TestEnvironment.IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        using var client = CreateAuthorizedClient();

        async Task<(double Value, double Reference, double AbsError, double WallS)> RunModeAsync(string mode)
        {
            var payload = new
            {
                a = 0.0,
                b = 10.0,
                n = 200_000,
                workers = 4,
                method = "trapezoid",
                order = "natural",
                mode
            };

            using var res = await client.PostAsync(
                "/v1/compute",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return (
                root.GetProperty("value").GetDouble(),
                root.GetProperty("reference").GetDouble(),
                root.GetProperty("abs_error").GetDouble(),
                root.GetProperty("wall_s").GetDouble());
        }

        var localRes = await RunModeAsync("local");
        var lockRes = await RunModeAsync("lock");
        var noneRes = await RunModeAsync("none");

        // Режими local та lock дають точний результат у межах допуску 1e-6
        Assert.True(localRes.AbsError <= 1.0e-6, $"local abs_error={localRes.AbsError} перевищує 1e-6");
        Assert.True(lockRes.AbsError <= 1.0e-6, $"lock abs_error={lockRes.AbsError} перевищує 1e-6");

        // Режим none при workers=4 оновлює спільну пам'ять без синхронізації і гарантовано втрачає оновлення
        Assert.True(noneRes.AbsError > 1.0, $"Очікувалася значна втрата оновлень у режимі none (abs_error > 1.0), отримано {noneRes.AbsError}");
        Assert.True(noneRes.Value < noneRes.Reference);
    }

    [SkippableTheory]
    [InlineData("{\"a\": 10, \"b\": 0, \"n\": 100, \"workers\": 1}", "invalid_interval_order")]
    [InlineData("{\"a\": -1, \"b\": 10, \"n\": 100, \"workers\": 1}", "invalid_interval_bounds")]
    [InlineData("{\"a\": 0, \"b\": 65, \"n\": 100, \"workers\": 1}", "invalid_interval_bounds")]
    [InlineData("{\"a\": 0, \"b\": 1, \"n\": 0, \"workers\": 1}", "invalid_subintervals")]
    [InlineData("{\"a\": 0, \"b\": 1, \"n\": 100, \"workers\": 3}", "invalid_workers")]
    [InlineData("{\"a\": 0, \"b\": 1, \"n\": 100, \"workers\": 2, \"method\": \"simpson\"}", "unsupported_method")]
    [InlineData("{\"a\": 0, \"b\": 1, \"n\": 100, \"workers\": 2, \"order\": \"reverse\"}", "unsupported_order")]
    [InlineData("{\"a\": 0, \"b\": 1, \"n\": 100, \"workers\": 2, \"mode\": \"atomic\"}", "unsupported_mode")]
    [InlineData("{\"a\": 0, \"b\": 1, \"n\": 100", "invalid_json_syntax")]
    public async Task Dpc_O21_PostCompute_InvalidParameters_Returns400ProblemDetails(string rawJson, string expectedErrorCode)
    {
        Skip.If(!await TestEnvironment.IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        using var client = CreateAuthorizedClient();
        using var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/compute", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedErrorCode, doc.RootElement.GetProperty("error_code").GetString());
    }

    [SkippableFact]
    public async Task Dpc_O21_PostCompute_MissingOrInvalidBearerToken_Returns401Unauthorized()
    {
        Skip.If(!await TestEnvironment.IsDatabaseAvailableAsync(), "PostgreSQL is not accessible. Integration test skipped.");

        var payload = new
        {
            a = 0.0,
            b = 10.0,
            n = 1_000,
            workers = 2,
            method = "trapezoid",
            order = "natural",
            mode = "local"
        };
        string jsonPayload = JsonSerializer.Serialize(payload);

        // 1. Запит без заголовка Authorization -> 401 Unauthorized
        using (var anonymousClient = _factory.CreateClient())
        {
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            using var resMissing = await anonymousClient.PostAsync("/v1/compute", content);
            Assert.Equal(HttpStatusCode.Unauthorized, resMissing.StatusCode);
            Assert.Equal("application/problem+json", resMissing.Content.Headers.ContentType?.MediaType);

            using var docMissing = JsonDocument.Parse(await resMissing.Content.ReadAsStringAsync());
            Assert.Equal("unauthorized", docMissing.RootElement.GetProperty("error_code").GetString());
        }

        // 2. Запит із недійсним Bearer-токеном -> 401 Unauthorized
        using (var wrongTokenClient = _factory.CreateClient())
        {
            wrongTokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token-value-123");
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            using var resInvalid = await wrongTokenClient.PostAsync("/v1/compute", content);
            Assert.Equal(HttpStatusCode.Unauthorized, resInvalid.StatusCode);
            Assert.Equal("application/problem+json", resInvalid.Content.Headers.ContentType?.MediaType);

            using var docInvalid = JsonDocument.Parse(await resInvalid.Content.ReadAsStringAsync());
            Assert.Equal("unauthorized", docInvalid.RootElement.GetProperty("error_code").GetString());
        }
    }
}
