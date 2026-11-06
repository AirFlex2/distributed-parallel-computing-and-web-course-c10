using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text.Json;

namespace EnrollmentService.Services.Compute;

// РПО ЛР2 (О2.1, О2.4): Окремий дочірній процес ОС для паралельного чисельного інтегрування.
// Запускається батьківським процесом через аргумент командного рядка "--compute-worker".
public static class ComputeWorkerProcess
{
    public const string WorkerArgFlag = "--compute-worker";
    public const string ProfileO23Flag = "--profile-o23";
    public const int SharedMemoryByteSize = 64;
    private const long MaxSharedMemoryUpdatesPerWorker = 4_000_000L;

    public static int Run(string[] args)
    {
        try
        {
            if (args.Any(a => string.Equals(a, ProfileO23Flag, StringComparison.OrdinalIgnoreCase)))
            {
                return RunProfileAndHypothesisVerification(args);
            }

            double a = 0.0;
            double b = 1.0;
            long n = 1;
            long startIdx = 0;
            long endIdx = 1;
            int workers = 1;
            double modelA = 50.0;
            double modelB = 5.0;
            double modelTau = 10.0;
            string mode = "local";
            string kernel = "composite";
            string? sharedFilePath = null;

            for (int i = 1; i < args.Length; i++)
            {
                string key = args[i];
                if (i + 1 >= args.Length)
                {
                    break;
                }

                string val = args[++i];
                switch (key)
                {
                    case "--a":
                        a = double.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--b":
                        b = double.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--n":
                        n = long.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--start":
                        startIdx = long.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--end":
                        endIdx = long.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--workers":
                        workers = int.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--model-a":
                        modelA = double.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--model-b":
                        modelB = double.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--model-tau":
                        modelTau = double.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "--mode":
                        mode = val.Trim().ToLowerInvariant();
                        break;
                    case "--kernel":
                        kernel = val.Trim().ToLowerInvariant();
                        break;
                    case "--shared-file":
                        sharedFilePath = val;
                        break;
                }
            }

            if (string.Equals(mode, "local", StringComparison.Ordinal))
            {
                double partialSum = string.Equals(kernel, "naive", StringComparison.Ordinal)
                    ? C10IntensityMath.ComputeTrapezoidSliceNaivePerSubinterval(
                        a, b, n, startIdx, endIdx, modelA, modelB, modelTau)
                    : C10IntensityMath.ComputeTrapezoidSliceLocal(
                        a, b, n, startIdx, endIdx, modelA, modelB, modelTau);

                Console.Out.WriteLine(partialSum.ToString("R", CultureInfo.InvariantCulture));
                Console.Out.Flush();
                return 0;
            }

            if (string.IsNullOrWhiteSpace(sharedFilePath) || !File.Exists(sharedFilePath))
            {
                Console.Error.WriteLine("Shared memory backing file is missing for mode=" + mode);
                return 2;
            }

            bool useLock = string.Equals(mode, "lock", StringComparison.Ordinal);
            bool useNaiveKernel = string.Equals(kernel, "naive", StringComparison.Ordinal);
            ExecuteSharedMemorySlice(
                sharedFilePath,
                useLock,
                useNaiveKernel,
                workers,
                a,
                b,
                n,
                startIdx,
                endIdx,
                modelA,
                modelB,
                modelTau);

            Console.Out.WriteLine("0");
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ComputeWorkerProcess error: {ex.Message}");
            return 1;
        }
    }

    private static unsafe void ExecuteSharedMemorySlice(
        string sharedFilePath,
        bool useLock,
        bool useNaiveKernel,
        int workers,
        double a,
        double b,
        long n,
        long startIdx,
        long endIdx,
        double modelA,
        double modelB,
        double modelTau)
    {
        using var fs = new FileStream(
            sharedFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        using var mmf = MemoryMappedFile.CreateFromFile(
            fs,
            mapName: null,
            capacity: SharedMemoryByteSize,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false);
        using var accessor = mmf.CreateViewAccessor(0, SharedMemoryByteSize, MemoryMappedFileAccess.ReadWrite);

        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            double* sharedSumPtr = (double*)(basePtr + 0);
            int* spinLockPtr = (int*)(basePtr + 8);
            int* readyCountPtr = (int*)(basePtr + 12);

            // Бар'єр синхронізації старту дочірніх процесів, щоб усі працівники одночасно зверталися до спільної пам'яті.
            // Усі процеси відмічаються у бар'єрі до перевірки порожнього діапазону, а цільова кількість очікуваних
            // активних процесів також враховує випадок n < workers.
            int activeWorkers = (a == b || n <= 0) ? 1 : (int)Math.Min(n, (long)workers);
            Interlocked.Increment(ref *readyCountPtr);
            if (activeWorkers > 1)
            {
                var swBarrier = Stopwatch.StartNew();
                while (Volatile.Read(ref *readyCountPtr) < activeWorkers && swBarrier.ElapsedMilliseconds < 2500)
                {
                    Thread.SpinWait(16);
                }
            }

            if (endIdx <= startIdx || a == b)
            {
                return;
            }

            long sliceCount = endIdx - startIdx;
            long batchStride = Math.Max(1L, sliceCount / MaxSharedMemoryUpdatesPerWorker);
            double h = (b - a) / n;
            double invNegTau = -1.0 / modelTau;

            for (long chunkStart = startIdx; chunkStart < endIdx; chunkStart += batchStride)
            {
                long chunkEnd = Math.Min(endIdx, chunkStart + batchStride);

                double contribution;
                if (useNaiveKernel)
                {
                    contribution = C10IntensityMath.ComputeTrapezoidSliceNaivePerSubinterval(
                        a, b, n, chunkStart, chunkEnd, modelA, modelB, modelTau);
                }
                else
                {
                    double tStart = a + chunkStart * h;
                    double tEnd = (chunkEnd == n) ? b : (a + chunkEnd * h);

                    double chunkSum = 0.5 * (modelB + modelA * Math.Exp(tStart * invNegTau));
                    for (long k = chunkStart + 1; k < chunkEnd; k++)
                    {
                        double t = a + k * h;
                        chunkSum += modelB + modelA * Math.Exp(t * invNegTau);
                    }
                    chunkSum += 0.5 * (modelB + modelA * Math.Exp(tEnd * invNegTau));

                    contribution = chunkSum * h;
                }

                if (useLock)
                {
                    // О2.4 (mode = "lock"): Захищене міжпроцесне оновлення спільної суми через атомарний спін-замок
                    while (Interlocked.CompareExchange(ref *spinLockPtr, 1, 0) != 0)
                    {
                        Thread.SpinWait(1);
                    }

                    double current = Volatile.Read(ref *sharedSumPtr);
                    Volatile.Write(ref *sharedSumPtr, current + contribution);
                    Volatile.Write(ref *spinLockPtr, 0);
                }
                else
                {
                    // О2.4 (mode = "none"): Незахищене міжпроцесне оновлення спільної суми (read-modify-write без синхронізації -> втрата оновлень)
                    double current = Volatile.Read(ref *sharedSumPtr);
                    if (workers > 1)
                    {
                        Thread.SpinWait(8);
                    }
                    Volatile.Write(ref *sharedSumPtr, current + contribution);
                }
            }

            accessor.Flush();
        }
        finally
        {
            if (basePtr != null)
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    // РПО О2.3: Відтворюваний профайлер фаз виконання та перевірка гіпотези оптимізації гарячого циклу
    // в ідентичному середовищі виконання (Release-контейнер).
    private static int RunProfileAndHypothesisVerification(string[] args)
    {
        long benchN = 250_000_000L;
        double a = 0.0;
        double b = 60.0;
        double modelA = 50.0;
        double modelB = 5.0;
        double modelTau = 10.0;

        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--n", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                benchN = long.Parse(args[++i], CultureInfo.InvariantCulture);
            }
        }

        // Розігрів JIT-компілятора перед заміром
        _ = C10IntensityMath.ComputeTrapezoidSliceNaivePerSubinterval(a, b, 100_000L, 0, 100_000L, modelA, modelB, modelTau);
        _ = C10IntensityMath.ComputeTrapezoidSliceLocal(a, b, 100_000L, 0, 100_000L, modelA, modelB, modelTau);

        // 1. Замір часу ДО оптимізації (наївне поелементне обчислення трапецій: 2*n викликів Math.Exp(-t / tau))
        var swNaive = Stopwatch.StartNew();
        TimeSpan cpuBeforeNaive = Process.GetCurrentProcess().TotalProcessorTime;
        double naiveValue = C10IntensityMath.ComputeTrapezoidSliceNaivePerSubinterval(
            a, b, benchN, 0, benchN, modelA, modelB, modelTau);
        TimeSpan cpuDeltaNaive = Process.GetCurrentProcess().TotalProcessorTime - cpuBeforeNaive;
        swNaive.Stop();

        // 2. Замір часу ПІСЛЯ оптимізації (складена формула зі спільними вузлами: n+1 викликів Math.Exp(t * invNegTau))
        var swOpt = Stopwatch.StartNew();
        TimeSpan cpuBeforeOpt = Process.GetCurrentProcess().TotalProcessorTime;
        double optValue = C10IntensityMath.ComputeTrapezoidSliceLocal(
            a, b, benchN, 0, benchN, modelA, modelB, modelTau);
        TimeSpan cpuDeltaOpt = Process.GetCurrentProcess().TotalProcessorTime - cpuBeforeOpt;
        swOpt.Stop();

        double reference = C10IntensityMath.ComputeAnalyticalIntegral(a, b, modelA, modelB, modelTau);
        double speedupRatio = swNaive.Elapsed.TotalSeconds / Math.Max(swOpt.Elapsed.TotalSeconds, 1e-9);

        var report = new Dictionary<string, object?>
        {
            ["tool"] = "EnrollmentService --compute-worker --profile-o23 (Stopwatch + Process.TotalProcessorTime)",
            ["bench_n"] = benchN,
            ["interval"] = new[] { a, b },
            ["reference"] = reference,
            ["before_optimization_naive"] = new Dictionary<string, object?>
            {
                ["description"] = "Naive per-subinterval trapezoid (2*n Math.Exp calls + division -t/tau)",
                ["math_exp_calls"] = 2L * benchN,
                ["wall_s"] = Math.Round(swNaive.Elapsed.TotalSeconds, 4),
                ["cpu_s"] = Math.Round(cpuDeltaNaive.TotalSeconds, 4),
                ["value"] = naiveValue,
                ["abs_error"] = Math.Abs(naiveValue - reference)
            },
            ["after_optimization_composite"] = new Dictionary<string, object?>
            {
                ["description"] = "Composite trapezoid with shared interior nodes ((n+1) Math.Exp calls + precomputed invNegTau)",
                ["math_exp_calls"] = benchN + 1L,
                ["wall_s"] = Math.Round(swOpt.Elapsed.TotalSeconds, 4),
                ["cpu_s"] = Math.Round(cpuDeltaOpt.TotalSeconds, 4),
                ["value"] = optValue,
                ["abs_error"] = Math.Abs(optValue - reference)
            },
            ["kernel_speedup_factor"] = Math.Round(speedupRatio, 3),
            ["value_difference"] = Math.Abs(optValue - naiveValue)
        };

        Console.Out.WriteLine(JsonSerializer.Serialize(report));
        Console.Out.Flush();
        return 0;
    }
}
