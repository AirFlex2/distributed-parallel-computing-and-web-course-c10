using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;

namespace EnrollmentService.Services.Compute;

// РПО ЛР2 (О2.1, О2.4): Окремий дочірній процес ОС для паралельного чисельного інтегрування.
// Запускається батьківським процесом через аргумент командного рядка "--compute-worker".
public static class ComputeWorkerProcess
{
    public const string WorkerArgFlag = "--compute-worker";
    public const int SharedMemoryByteSize = 64;
    private const long MaxSharedMemoryUpdatesPerWorker = 4_000_000L;

    public static int Run(string[] args)
    {
        try
        {
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
                    case "--shared-file":
                        sharedFilePath = val;
                        break;
                }
            }

            if (string.Equals(mode, "local", StringComparison.Ordinal))
            {
                double partialSum = C10IntensityMath.ComputeTrapezoidSliceLocal(
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
            ExecuteSharedMemorySlice(
                sharedFilePath,
                useLock,
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
        if (endIdx <= startIdx || a == b)
        {
            return;
        }

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

            // Бар'єр синхронізації старту дочірніх процесів, щоб усі працівники одночасно зверталися до спільної пам'яті
            Interlocked.Increment(ref *readyCountPtr);
            if (workers > 1)
            {
                var swBarrier = Stopwatch.StartNew();
                while (Volatile.Read(ref *readyCountPtr) < workers && swBarrier.ElapsedMilliseconds < 2500)
                {
                    Thread.SpinWait(16);
                }
            }

            long sliceCount = endIdx - startIdx;
            long batchStride = Math.Max(1L, sliceCount / MaxSharedMemoryUpdatesPerWorker);
            double h = (b - a) / n;
            double invNegTau = -1.0 / modelTau;

            for (long chunkStart = startIdx; chunkStart < endIdx; chunkStart += batchStride)
            {
                long chunkEnd = Math.Min(endIdx, chunkStart + batchStride);

                double tStart = a + chunkStart * h;
                double tEnd = (chunkEnd == n) ? b : (a + chunkEnd * h);

                double chunkSum = 0.5 * (modelB + modelA * Math.Exp(tStart * invNegTau));
                for (long k = chunkStart + 1; k < chunkEnd; k++)
                {
                    double t = a + k * h;
                    chunkSum += modelB + modelA * Math.Exp(t * invNegTau);
                }
                chunkSum += 0.5 * (modelB + modelA * Math.Exp(tEnd * invNegTau));

                double contribution = chunkSum * h;

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
}
