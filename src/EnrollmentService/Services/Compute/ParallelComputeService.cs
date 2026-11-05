using System.Diagnostics;
using System.Globalization;
using EnrollmentService.Domain;

namespace EnrollmentService.Services.Compute;

// РПО ЛР2 (О2.1–О2.4): Сервіс багатопроцесного чисельного інтегрування функції інтенсивності C10
// λ(t) = B + A · exp(-t / τ) методом трапецій за допомогою окремих процесів ОС.
public sealed class ParallelComputeService
{
    private static readonly HashSet<int> AllowedWorkers = [1, 2, 4];
    private static readonly HashSet<string> AllowedModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "local",
        "lock",
        "none"
    };
    private static readonly HashSet<string> AllowedKernels = new(StringComparer.OrdinalIgnoreCase)
    {
        "composite",
        "naive"
    };

    private readonly ServiceOptions _options;

    public ParallelComputeService(ServiceOptions options)
    {
        _options = options;
    }

    public async Task<ServiceResult<ComputeResponseDto>> ComputeAsync(
        ComputeRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!request.A.HasValue || !request.B.HasValue ||
            double.IsNaN(request.A.Value) || double.IsInfinity(request.A.Value) ||
            double.IsNaN(request.B.Value) || double.IsInfinity(request.B.Value))
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_interval",
                "Некоректні межі інтегрування",
                "Параметри 'a' та 'b' є обов'язковими скінченними дійсними числами.");
        }

        double a = request.A.Value;
        double b = request.B.Value;

        double modelA = request.ModelA ?? _options.ComputeModelA;
        double modelB = request.ModelB ?? _options.ComputeModelB;
        double modelTau = request.ModelTau ?? _options.ComputeModelTau;
        double modelT = request.ModelT ?? _options.ComputeModelT;

        if (double.IsNaN(modelA) || double.IsInfinity(modelA) || modelA < 0.0 ||
            double.IsNaN(modelB) || double.IsInfinity(modelB) || modelB < 0.0 ||
            double.IsNaN(modelTau) || double.IsInfinity(modelTau) || modelTau <= 0.0 ||
            double.IsNaN(modelT) || double.IsInfinity(modelT) || modelT <= 0.0)
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_model_parameters",
                "Некоректні параметри математичної моделі C10",
                "Параметри моделі повинні задовольняти умови: A >= 0, B >= 0, τ > 0, T > 0.");
        }

        if (a > b)
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_interval_order",
                "Некоректний інтервал інтегрування",
                $"Нижня межа інтегрування a ({a.ToString(CultureInfo.InvariantCulture)}) не може перевищувати верхню межу b ({b.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (a < 0.0 || b > modelT)
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_interval_bounds",
                "Межі інтегрування поза областю визначення моделі C10",
                $"За карткою теми C10 функція інтенсивності λ(t) визначена на відрізку [0, T]. Має виконуватися умова 0 <= a <= b <= T (T = {modelT.ToString(CultureInfo.InvariantCulture)}), але передано [{a.ToString(CultureInfo.InvariantCulture)}, {b.ToString(CultureInfo.InvariantCulture)}].");
        }

        if (!request.N.HasValue || request.N.Value < 1 || request.N.Value > 2_000_000_000L)
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_subintervals",
                "Некоректна кількість підінтервалів n",
                "Параметр 'n' є обов'язковим цілим числом у діапазоні [1, 2000000000].");
        }

        long n = request.N.Value;

        if (!request.Workers.HasValue || !AllowedWorkers.Contains(request.Workers.Value))
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_workers",
                "Непідтримувана кількість процесів-працівників",
                "Параметр 'workers' є обов'язковим і приймає лише значення 1, 2 або 4.");
        }

        int workers = request.Workers.Value;

        string method = string.IsNullOrWhiteSpace(request.Method)
            ? "trapezoid"
            : request.Method.Trim().ToLowerInvariant();
        if (!string.Equals(method, "trapezoid", StringComparison.Ordinal))
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "unsupported_method",
                "Непідтримуваний чисельний метод",
                $"Метод '{request.Method}' не підтримується. Єдине допустиме значення в ЛР2: 'trapezoid'.");
        }

        string order = string.IsNullOrWhiteSpace(request.Order)
            ? "natural"
            : request.Order.Trim().ToLowerInvariant();
        if (!string.Equals(order, "natural", StringComparison.Ordinal))
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "unsupported_order",
                "Непідтримуваний порядок підсумовування",
                $"Порядок '{request.Order}' не підтримується. Єдине допустиме значення в ЛР2: 'natural'.");
        }

        string mode = string.IsNullOrWhiteSpace(request.Mode)
            ? "local"
            : request.Mode.Trim().ToLowerInvariant();
        if (!AllowedModes.Contains(mode))
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "unsupported_mode",
                "Непідтримуваний режим агрегації результатів",
                $"Режим '{request.Mode}' не підтримується. Допустимі значення: 'local', 'lock', 'none'.");
        }

        string kernel = string.IsNullOrWhiteSpace(request.Kernel)
            ? "composite"
            : request.Kernel.Trim().ToLowerInvariant();
        if (!AllowedKernels.Contains(kernel))
        {
            return ServiceResult<ComputeResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "unsupported_kernel",
                "Непідтримуваний варіант обчислювального ядра",
                $"Варіант ядра '{request.Kernel}' не підтримується. Допустимі значення: 'composite', 'naive'.");
        }

        string? sharedFilePath = null;
        if (!string.Equals(mode, "local", StringComparison.Ordinal))
        {
            sharedFilePath = Path.Combine(
                Path.GetTempPath(),
                $"c10_compute_shm_{Guid.NewGuid():N}.bin");

            await using var initFs = new FileStream(
                sharedFilePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            byte[] zeroBuffer = new byte[ComputeWorkerProcess.SharedMemoryByteSize];
            await initFs.WriteAsync(zeroBuffer, cancellationToken);
            await initFs.FlushAsync(cancellationToken);
        }

        try
        {
            // О2.1 та О2.3: Повний час виконання (wall_s) охоплює створення дочірніх процесів ОС,
            // передачу параметрів, обчислення на піддіапазонах, збір локальних результатів і підсумовування.
            var swTotal = Stopwatch.StartNew();

            var processes = new Process[workers];
            var stdoutTasks = new Task<string>[workers];
            var stderrTasks = new Task<string>[workers];

            try
            {
                for (int w = 0; w < workers; w++)
                {
                    var (startIdx, endIdx) = C10IntensityMath.GetWorkerSubintervalBounds(n, workers, w);
                    var psi = CreateWorkerStartInfo(
                        a, b, n, startIdx, endIdx, workers,
                        modelA, modelB, modelTau, mode, kernel, sharedFilePath);

                    var proc = new Process { StartInfo = psi };
                    if (!proc.Start())
                    {
                        throw new InvalidOperationException($"Не вдалося запустити дочірній процес-працівник #{w}.");
                    }

                    processes[w] = proc;
                    stdoutTasks[w] = proc.StandardOutput.ReadToEndAsync(cancellationToken);
                    stderrTasks[w] = proc.StandardError.ReadToEndAsync(cancellationToken);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, _options.ComputeWorkerTimeoutMs)));

                for (int w = 0; w < workers; w++)
                {
                    await processes[w].WaitForExitAsync(timeoutCts.Token);
                }

                double computedValue = 0.0;

                if (string.Equals(mode, "local", StringComparison.Ordinal))
                {
                    // О2.1 та О2.4 (mode = "local"): Детерміноване підсумовування локальних результатів
                    // у фіксованому порядку індексів працівників w = 0 .. workers - 1 (order = "natural").
                    for (int w = 0; w < workers; w++)
                    {
                        string stdout = (await stdoutTasks[w]).Trim();
                        string stderr = (await stderrTasks[w]).Trim();

                        if (processes[w].ExitCode != 0)
                        {
                            return ServiceResult<ComputeResponseDto>.Failure(
                                StatusCodes.Status500InternalServerError,
                                "compute_worker_failed",
                                "Помилка дочірнього процесу обчислення",
                                $"Процес-працівник #{w} завершився з кодом {processes[w].ExitCode}: {stderr}");
                        }

                        if (!double.TryParse(stdout, NumberStyles.Float, CultureInfo.InvariantCulture, out double workerPartialSum))
                        {
                            return ServiceResult<ComputeResponseDto>.Failure(
                                StatusCodes.Status500InternalServerError,
                                "compute_worker_invalid_output",
                                "Некоректний формат результату дочірнього процесу",
                                $"Процес-працівник #{w} повернув нечисловий вивід: '{stdout}'.");
                        }

                        computedValue += workerPartialSum;
                    }
                }
                else
                {
                    for (int w = 0; w < workers; w++)
                    {
                        string stderr = (await stderrTasks[w]).Trim();
                        if (processes[w].ExitCode != 0)
                        {
                            return ServiceResult<ComputeResponseDto>.Failure(
                                StatusCodes.Status500InternalServerError,
                                "compute_worker_failed",
                                "Помилка дочірнього процесу обчислення у спільній пам'яті",
                                $"Процес-працівник #{w} завершився з кодом {processes[w].ExitCode}: {stderr}");
                        }
                    }

                    byte[] shmBytes = await File.ReadAllBytesAsync(sharedFilePath!, cancellationToken);
                    computedValue = BitConverter.ToDouble(shmBytes, 0);
                }

                swTotal.Stop();

                double wallS = swTotal.Elapsed.TotalSeconds;
                double durationMs = swTotal.Elapsed.TotalMilliseconds;
                double reference = C10IntensityMath.ComputeAnalyticalIntegral(a, b, modelA, modelB, modelTau);
                double absError = Math.Abs(computedValue - reference);
                double relError = absError / Math.Max(Math.Abs(reference), 1e-15);

                var details = new ComputeResultDetailsDto(
                    computedValue,
                    reference,
                    absError,
                    absError,
                    relError,
                    wallS,
                    durationMs,
                    workers,
                    method,
                    n,
                    order,
                    mode);

                var response = new ComputeResponseDto(
                    computedValue,
                    reference,
                    absError,
                    absError,
                    relError,
                    wallS,
                    durationMs,
                    workers,
                    method,
                    n,
                    order,
                    mode,
                    a,
                    b,
                    "done",
                    _options.InstanceId,
                    details);

                StructuredJsonLogger.LogEvent(_options.InstanceId, "dpc.compute.completed", new Dictionary<string, object?>
                {
                    ["a"] = a,
                    ["b"] = b,
                    ["n"] = n,
                    ["workers"] = workers,
                    ["method"] = method,
                    ["order"] = order,
                    ["mode"] = mode,
                    ["value"] = computedValue,
                    ["reference"] = reference,
                    ["abs_error"] = absError,
                    ["rel_error"] = relError,
                    ["wall_s"] = wallS,
                    ["duration_ms"] = durationMs
                });

                return ServiceResult<ComputeResponseDto>.Ok(response);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillRemainingProcesses(processes);
                return ServiceResult<ComputeResponseDto>.Failure(
                    StatusCodes.Status504GatewayTimeout,
                    "compute_worker_timeout",
                    "Перевищено час очікування дочірніх процесів обчислення",
                    $"Дочірні процеси не завершилися протягом ліміту {_options.ComputeWorkerTimeoutMs} мс.");
            }
            finally
            {
                KillRemainingProcesses(processes);
                foreach (var proc in processes)
                {
                    proc?.Dispose();
                }
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sharedFilePath))
            {
                try
                {
                    if (File.Exists(sharedFilePath))
                    {
                        File.Delete(sharedFilePath);
                    }
                }
                catch
                {
                    // Ігноруємо помилку видалення тимчасового файлу спільної пам'яті
                }
            }
        }
    }

    private static void KillRemainingProcesses(Process?[] processes)
    {
        foreach (var proc in processes)
        {
            if (proc == null)
            {
                continue;
            }
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Процес уже завершився
            }
        }
    }

    private static ProcessStartInfo CreateWorkerStartInfo(
        double a,
        double b,
        long n,
        long startIdx,
        long endIdx,
        int workers,
        double modelA,
        double modelB,
        double modelTau,
        string mode,
        string kernel,
        string? sharedFilePath)
    {
        string assemblyLocation = typeof(Program).Assembly.Location;
        string? currentProcessPath = Environment.ProcessPath;

        string fileName = "dotnet";
        if (!string.IsNullOrWhiteSpace(currentProcessPath))
        {
            string procFile = Path.GetFileNameWithoutExtension(currentProcessPath);
            if (string.Equals(procFile, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                fileName = currentProcessPath;
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add(assemblyLocation);
        psi.ArgumentList.Add(ComputeWorkerProcess.WorkerArgFlag);
        psi.ArgumentList.Add("--a");
        psi.ArgumentList.Add(a.ToString("R", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--b");
        psi.ArgumentList.Add(b.ToString("R", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--n");
        psi.ArgumentList.Add(n.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--start");
        psi.ArgumentList.Add(startIdx.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--end");
        psi.ArgumentList.Add(endIdx.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--workers");
        psi.ArgumentList.Add(workers.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--model-a");
        psi.ArgumentList.Add(modelA.ToString("R", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--model-b");
        psi.ArgumentList.Add(modelB.ToString("R", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--model-tau");
        psi.ArgumentList.Add(modelTau.ToString("R", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("--kernel");
        psi.ArgumentList.Add(kernel);

        if (!string.IsNullOrWhiteSpace(sharedFilePath))
        {
            psi.ArgumentList.Add("--shared-file");
            psi.ArgumentList.Add(sharedFilePath);
        }

        return psi;
    }
}
