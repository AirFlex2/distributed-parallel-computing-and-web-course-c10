using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MetricsMeasurer;

class Program
{
    static async Task<int> Main(string[] args)
    {
        string baseUrl = args.Length > 0 ? args[0] : "http://localhost:8080";
        string token = args.Length > 1 ? args[1] : "secret-c10-token-2026";
        int totalRequests = 500;
        int concurrency = 50;

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"[1/5] Ініціалізація вимірювача метрик на {baseUrl} (N={totalRequests}, concurrency={concurrency})");

        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Скидання бази даних
        Console.WriteLine("[2/5] Скидання стану черги (/v1/admin/reset)...");
        var resetResp = await client.PostAsync("/v1/admin/reset?unsafeMode=false", new StringContent("{}", Encoding.UTF8, "application/json"));
        if (!resetResp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Помилка скидання: {resetResp.StatusCode}");
            return 1;
        }

        // 2. Цільова група стенду РПО: DISC-DPC-01 (підтримує dpc_sandbox_enabled)
        string targetGroupId = "DISC-DPC-01";

        var http202LatenciesMs = new ConcurrentBag<double>();
        var acceptedIds = new ConcurrentBag<string>();
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);

        Console.WriteLine($"[3/5] Надсилання {totalRequests} запитів та опитування результатів (50 потоків)...");
        var totalSw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(1, totalRequests).Select(async reqNum =>
        {
            await semaphore.WaitAsync();
            try
            {
                string studentId = $"student-perf-{reqNum:D3}";
                string groupId = targetGroupId;
                string body = $"{{\"student_id\":\"{studentId}\",\"group_id\":\"{groupId}\"}}";

                string? acceptedId = null;
                while (acceptedId == null)
                {
                    var postSw = Stopwatch.StartNew();
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var postResp = await client.PostAsync("/v1/enrollment-requests", content);
                    postSw.Stop();

                    if ((int)postResp.StatusCode == 202)
                    {
                        http202LatenciesMs.Add(postSw.Elapsed.TotalMilliseconds);
                        var json = await postResp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        acceptedId = doc.RootElement.GetProperty("id").GetString();
                        acceptedIds.Add(acceptedId!);
                    }
                    else if ((int)postResp.StatusCode == 429)
                    {
                        await Task.Delay(30);
                    }
                    else
                    {
                        Console.WriteLine($"Помилка {postResp.StatusCode} для {studentId}");
                        break;
                    }
                }

                if (acceptedId != null)
                {
                    // Опитування до термінального статусу (фіксує FirstReadTimestamp у DPC)
                    bool reachedTerminal = false;
                    for (int poll = 0; poll < 400; poll++)
                    {
                        await Task.Delay(100);
                        using var getResp = await client.GetAsync($"/v1/enrollment-requests/{acceptedId}");
                        if (getResp.IsSuccessStatusCode)
                        {
                            var getText = await getResp.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(getText);
                            string? st = doc.RootElement.GetProperty("status").GetString();
                            if (st is "done" or "rejected" or "failed")
                            {
                                reachedTerminal = true;
                                break;
                            }
                        }
                    }

                    if (!reachedTerminal)
                    {
                        throw new InvalidOperationException($"Заявка {acceptedId} для студента {studentId} не досягла термінального стану за час очікування!");
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        totalSw.Stop();
        Console.WriteLine($"Всі {totalRequests} запитів завершено за {totalSw.Elapsed.TotalSeconds:F2} с!");

        // 3. Контрольне опитування всіх ID для гарантії, що first_read зафіксовано для кожного
        Console.WriteLine("[4/5] Контрольний збір FirstRead...");
        foreach (var id in acceptedIds)
        {
            try
            {
                await client.GetAsync($"/v1/enrollment-requests/{id}");
            }
            catch { }
        }

        // 4. Отримання фінальних метрик
        Console.WriteLine("[5/5] Отримання GET /metrics...");
        var metricsResp = await client.GetAsync("/metrics");
        var metricsJson = await metricsResp.Content.ReadAsStringAsync();
        using var mDoc = JsonDocument.Parse(metricsJson);
        var root = mDoc.RootElement;

        var counters = root.GetProperty("counters");
        int totalAccepted = counters.GetProperty("total_accepted").GetInt32();
        int totalDone = counters.GetProperty("total_done").GetInt32();
        int totalRejected = counters.GetProperty("total_rejected").GetInt32();
        int totalFailed = counters.GetProperty("total_failed").GetInt32();
        int violations = counters.GetProperty("monotonic_order_violations").GetInt32();

        var intervals = root.GetProperty("intervals_ms");
        var qWait = intervals.GetProperty("queue_wait_ms");
        var proc = intervals.GetProperty("processing_time_ms");
        var deliv = intervals.GetProperty("delivery_wait_ms");
        var full = intervals.GetProperty("full_domain_execution_ms");

        var latList = http202LatenciesMs.OrderBy(x => x).ToList();
        double GetPercentile(List<double> list, double p)
        {
            if (list.Count == 0) return 0;
            int idx = (int)Math.Floor((list.Count - 1) * p);
            return Math.Round(list[idx], 1);
        }

        double httpP50 = GetPercentile(latList, 0.50);
        double httpP95 = GetPercentile(latList, 0.95);
        double httpP99 = GetPercentile(latList, 0.99);

        Console.WriteLine("\n======================= РЕЗУЛЬТАТИ ВИМІРЮВАННЯ (N=500) =======================");
        Console.WriteLine($"total_accepted: {totalAccepted}, total_done: {totalDone}, total_rejected: {totalRejected}, total_failed: {totalFailed}, violations: {violations}");
        Console.WriteLine($"HTTP 202 (мс): p50={httpP50}, p95={httpP95}, p99={httpP99}");
        Console.WriteLine($"queue_wait_ms: sample_count={qWait.GetProperty("sample_count").GetInt32()}, p50={qWait.GetProperty("p50_ms").GetDouble()}, p95={qWait.GetProperty("p95_ms").GetDouble()}, p99={qWait.GetProperty("p99_ms").GetDouble()}");
        Console.WriteLine($"processing_time_ms: sample_count={proc.GetProperty("sample_count").GetInt32()}, p50={proc.GetProperty("p50_ms").GetDouble()}, p95={proc.GetProperty("p95_ms").GetDouble()}, p99={proc.GetProperty("p99_ms").GetDouble()}");
        Console.WriteLine($"delivery_wait_ms: sample_count={deliv.GetProperty("sample_count").GetInt32()}, p50={deliv.GetProperty("p50_ms").GetDouble()}, p95={deliv.GetProperty("p95_ms").GetDouble()}, p99={deliv.GetProperty("p99_ms").GetDouble()}");
        Console.WriteLine($"full_domain_execution_ms: sample_count={full.GetProperty("sample_count").GetInt32()}, p50={full.GetProperty("p50_ms").GetDouble()}, p95={full.GetProperty("p95_ms").GetDouble()}, p99={full.GetProperty("p99_ms").GetDouble()}");

        // Сувора валідація результатів перед збереженням у файл (Пункт 4)
        if (totalAccepted != totalRequests)
        {
            throw new InvalidOperationException($"Валідація не пройдена: total_accepted = {totalAccepted}, очікувалося {totalRequests}");
        }

        int totalFinished = totalDone + totalRejected + totalFailed;
        if (totalFinished != totalRequests)
        {
            throw new InvalidOperationException($"Валідація не пройдена: total_finished = {totalFinished}, очікувалося {totalRequests}");
        }

        int qCount = qWait.GetProperty("sample_count").GetInt32();
        int pCount = proc.GetProperty("sample_count").GetInt32();
        int dCount = deliv.GetProperty("sample_count").GetInt32();
        int fCount = full.GetProperty("sample_count").GetInt32();

        if (qCount != totalRequests || pCount != totalRequests || dCount != totalRequests || fCount != totalRequests)
        {
            throw new InvalidOperationException($"Валідація не пройдена: sample_count не дорівнює {totalRequests} для всіх інтервалів (q={qCount}, p={pCount}, d={dCount}, f={fCount})");
        }

        if (acceptedIds.Count != totalRequests)
        {
            throw new InvalidOperationException($"Валідація не пройдена: кількість успішно прийнятих заявок ({acceptedIds.Count}) не дорівнює {totalRequests}");
        }

        if (totalFailed > 0)
        {
            throw new InvalidOperationException($"Валідація не пройдена: зафіксовано {totalFailed} збоїв воркерів (total_failed > 0)");
        }

        var resultObject = new
        {
            measured_at_utc = DateTimeOffset.UtcNow,
            sample_size = totalRequests,
            concurrency = concurrency,
            counters = new
            {
                total_accepted = totalAccepted,
                total_done = totalDone,
                total_rejected = totalRejected,
                total_failed = totalFailed,
                monotonic_order_violations = violations
            },
            http_202_ms = new
            {
                p50 = httpP50,
                p95 = httpP95,
                p99 = httpP99
            },
            intervals_ms = new
            {
                queue_wait_ms = new
                {
                    sample_count = qWait.GetProperty("sample_count").GetInt32(),
                    p50_ms = qWait.GetProperty("p50_ms").GetDouble(),
                    p95_ms = qWait.GetProperty("p95_ms").GetDouble(),
                    p99_ms = qWait.GetProperty("p99_ms").GetDouble()
                },
                processing_time_ms = new
                {
                    sample_count = proc.GetProperty("sample_count").GetInt32(),
                    p50_ms = proc.GetProperty("p50_ms").GetDouble(),
                    p95_ms = proc.GetProperty("p95_ms").GetDouble(),
                    p99_ms = proc.GetProperty("p99_ms").GetDouble()
                },
                delivery_wait_ms = new
                {
                    sample_count = deliv.GetProperty("sample_count").GetInt32(),
                    p50_ms = deliv.GetProperty("p50_ms").GetDouble(),
                    p95_ms = deliv.GetProperty("p95_ms").GetDouble(),
                    p99_ms = deliv.GetProperty("p99_ms").GetDouble()
                },
                full_domain_execution_ms = new
                {
                    sample_count = full.GetProperty("sample_count").GetInt32(),
                    p50_ms = full.GetProperty("p50_ms").GetDouble(),
                    p95_ms = full.GetProperty("p95_ms").GetDouble(),
                    p99_ms = full.GetProperty("p99_ms").GetDouble()
                }
            }
        };

        string outJson = JsonSerializer.Serialize(resultObject, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync("scripts/empirical_metrics_500.json", outJson);
        Console.WriteLine("Дані успішно збережено у scripts/empirical_metrics_500.json");

        return 0;
    }
}
