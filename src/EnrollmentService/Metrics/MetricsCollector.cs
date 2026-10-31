using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using EnrollmentService.Queues;

namespace EnrollmentService.Metrics;

// ============================================================================
// ПУНКТ О1.5 (Час виконання й метрики) та ПУНКТ Д1.2 (Перевірка закону Літтла)
// Містить як точний часово-зважений інтеграл черги, так і незалежне періодичне
// опитування queue_length за фіксованим таймером (100 мс) без прив'язки до подій.
// ============================================================================
public sealed class MetricsCollector
{
    private readonly object _stateLock = new();

    private long _inProcessingCount;
    private long _totalAccepted;
    private long _totalDone;
    private long _totalRejected;
    private long _totalFailed;
    private long _total429;
    private long _monotonicOrderViolations;

    public long InProcessingCount => Interlocked.Read(ref _inProcessingCount);
    public long TotalAccepted => Interlocked.Read(ref _totalAccepted);
    public long TotalDone => Interlocked.Read(ref _totalDone);
    public long TotalRejected => Interlocked.Read(ref _totalRejected);
    public long TotalFailed => Interlocked.Read(ref _totalFailed);

    private readonly ConcurrentDictionary<Guid, QueuedRequestItem> _trackedItems = new();
    private readonly ConcurrentQueue<double> _queueWaitSamplesMs = new();
    private readonly ConcurrentQueue<double> _processingTimeSamplesMs = new();
    private readonly ConcurrentQueue<double> _deliveryWaitSamplesMs = new();
    private readonly ConcurrentQueue<double> _totalExecutionSamplesMs = new();

    // Поля для закону Літтла L = lambda * W за єдиний заліковий інтервал:
    // 1) Точне часове зважування: інтеграл Q(t) dt / T
    // 2) Незалежна дискретна вибірка: PeriodicTimer (кожні 100 мс читає queue.Count)
    private bool _windowStarted;
    private long _windowStartTimestamp = Stopwatch.GetTimestamp();
    private long _lastQueueChangeTimestamp = Stopwatch.GetTimestamp();
    private int _currentQueueLength;
    private double _weightedQueueIntegralSec;
    private long _windowArrivalsCount;
    private readonly List<double> _windowQueueWaitSamplesMs = new();
    private readonly List<(long Timestamp, int QueueLength)> _windowQueueSnapshots = new();
    private readonly List<int> _independentPeriodicQueueSamples = new();

    public bool TryEnqueueAndRecordAccepted(IBoundedRequestQueue queue, QueuedRequestItem item)
    {
        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            item.EnqueueTimestamp = now;

            if (!queue.TryEnqueue(item))
            {
                return false;
            }

            RecordAcceptedLocked(item, now);
            return true;
        }
    }

    public void RecordAccepted(QueuedRequestItem item, int currentQueueLength)
    {
        lock (_stateLock)
        {
            long now = item.EnqueueTimestamp > 0 ? item.EnqueueTimestamp : Stopwatch.GetTimestamp();
            item.EnqueueTimestamp = now;
            RecordAcceptedLocked(item, now, currentQueueLength);
        }
    }

    private void RecordAcceptedLocked(QueuedRequestItem item, long now, int? explicitQueueLength = null)
    {
        Interlocked.Increment(ref _totalAccepted);
        _trackedItems[item.Id] = item;

        if (!_windowStarted)
        {
            _windowStartTimestamp = now;
            _lastQueueChangeTimestamp = now;
            _currentQueueLength = 0;
            _weightedQueueIntegralSec = 0.0;
            _independentPeriodicQueueSamples.Clear();
            _windowStarted = true;
        }
        else if (now > _lastQueueChangeTimestamp)
        {
            double dtSec = Stopwatch.GetElapsedTime(_lastQueueChangeTimestamp, now).TotalSeconds;
            _weightedQueueIntegralSec += _currentQueueLength * dtSec;
            _lastQueueChangeTimestamp = now;
        }

        _currentQueueLength = explicitQueueLength ?? (_currentQueueLength + 1);
        _windowArrivalsCount++;
        _windowQueueSnapshots.Add((now, _currentQueueLength));
    }

    // Д1.2: Незалежне періодичне зняття фактичної довжини черги queue.Count за зовнішнім таймером (кожні 100 мс)
    public void RecordIndependentPeriodicSample(int actualQueueCount)
    {
        lock (_stateLock)
        {
            if (_windowStarted)
            {
                _independentPeriodicQueueSamples.Add(actualQueueCount);
            }
        }
    }

    public void RecordRejected429()
    {
        Interlocked.Increment(ref _total429);
    }

    public void RecordWorkerStarted(QueuedRequestItem item, int currentQueueLength)
    {
        double queueWaitMs;
        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            long enqueueTs = Interlocked.Read(ref item.EnqueueTimestamp);
            if (now < enqueueTs)
            {
                now = enqueueTs;
            }

            Interlocked.Exchange(ref item.StartProcessingTimestamp, now);

            if (_windowStarted && now > _lastQueueChangeTimestamp)
            {
                double dtSec = Stopwatch.GetElapsedTime(_lastQueueChangeTimestamp, now).TotalSeconds;
                _weightedQueueIntegralSec += _currentQueueLength * dtSec;
                _lastQueueChangeTimestamp = now;
            }

            _currentQueueLength = Math.Max(0, _currentQueueLength - 1);
            _windowQueueSnapshots.Add((now, _currentQueueLength));

            queueWaitMs = Stopwatch.GetElapsedTime(enqueueTs, now).TotalMilliseconds;

            if (_windowStarted)
            {
                long effectiveArrival = Math.Max(enqueueTs, _windowStartTimestamp);
                double windowWaitMs = Stopwatch.GetElapsedTime(effectiveArrival, now).TotalMilliseconds;
                _windowQueueWaitSamplesMs.Add(windowWaitMs);
            }
        }

        Interlocked.Increment(ref _inProcessingCount);

        if (Interlocked.Read(ref item.StartProcessingTimestamp) < Interlocked.Read(ref item.EnqueueTimestamp))
        {
            Interlocked.Increment(ref _monotonicOrderViolations);
        }

        _queueWaitSamplesMs.Enqueue(queueWaitMs);
    }

    // О1.5: Фіксує монотонну позначку завершення обробки ДО публікації кінцевого статусу в БД
    public DateTimeOffset MarkProcessingFinished(QueuedRequestItem item)
    {
        long now = Stopwatch.GetTimestamp();
        long startTs = Interlocked.Read(ref item.StartProcessingTimestamp);
        if (startTs > 0 && now < startTs)
        {
            now = startTs;
        }

        Interlocked.CompareExchange(ref item.FinishProcessingTimestamp, now, 0);
        return DateTimeOffset.UtcNow;
    }

    public void RecordWorkerFinished(QueuedRequestItem item, string finalStatus, int currentQueueLength)
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref item.FinishProcessingTimestamp, now, 0);

        long finishTs = Interlocked.Read(ref item.FinishProcessingTimestamp);
        long startTs = Interlocked.Read(ref item.StartProcessingTimestamp);
        long enqueueTs = Interlocked.Read(ref item.EnqueueTimestamp);

        Interlocked.Decrement(ref _inProcessingCount);

        if (finishTs < startTs)
        {
            Interlocked.Increment(ref _monotonicOrderViolations);
        }

        double procMs = Stopwatch.GetElapsedTime(startTs, finishTs).TotalMilliseconds;
        double totalMs = Stopwatch.GetElapsedTime(enqueueTs, finishTs).TotalMilliseconds;

        _processingTimeSamplesMs.Enqueue(procMs);
        _totalExecutionSamplesMs.Enqueue(totalMs);

        TryRecordDeliverySample(item);

        switch (finalStatus)
        {
            case "done":
                Interlocked.Increment(ref _totalDone);
                break;
            case "rejected":
                Interlocked.Increment(ref _totalRejected);
                break;
            case "failed":
                Interlocked.Increment(ref _totalFailed);
                break;
        }
    }

    public void RecordFirstReadIfCompleted(Guid id)
    {
        if (_trackedItems.TryGetValue(id, out var item))
        {
            long now = Stopwatch.GetTimestamp();
            if (Interlocked.CompareExchange(ref item.FirstReadTimestamp, now, 0) == 0)
            {
                TryRecordDeliverySample(item);
            }
        }
    }

    private void TryRecordDeliverySample(QueuedRequestItem item)
    {
        long finishTs = Interlocked.Read(ref item.FinishProcessingTimestamp);
        long firstReadTs = Interlocked.Read(ref item.FirstReadTimestamp);

        if (finishTs > 0 && firstReadTs > 0)
        {
            if (Interlocked.CompareExchange(ref item.DeliverySampleRecorded, 1, 0) == 0)
            {
                if (firstReadTs < finishTs)
                {
                    Interlocked.Increment(ref _monotonicOrderViolations);
                }

                double deliveryWaitMs = Stopwatch.GetElapsedTime(finishTs, firstReadTs).TotalMilliseconds;
                _deliveryWaitSamplesMs.Enqueue(deliveryWaitMs);
            }
        }
    }

    public (double? QueueWaitMs, double? ProcessingMs, double? DeliveryWaitMs) GetItemTimings(Guid id)
    {
        if (!_trackedItems.TryGetValue(id, out var item))
        {
            return (null, null, null);
        }

        long enqueueTs = Interlocked.Read(ref item.EnqueueTimestamp);
        long startTs = Interlocked.Read(ref item.StartProcessingTimestamp);
        long finishTs = Interlocked.Read(ref item.FinishProcessingTimestamp);
        long firstReadTs = Interlocked.Read(ref item.FirstReadTimestamp);

        double? qWait = startTs > 0 && enqueueTs > 0
            ? Math.Round(Stopwatch.GetElapsedTime(enqueueTs, startTs).TotalMilliseconds, 3)
            : null;

        double? proc = finishTs > 0 && startTs > 0
            ? Math.Round(Stopwatch.GetElapsedTime(startTs, finishTs).TotalMilliseconds, 3)
            : null;

        double? deliv = firstReadTs > 0 && finishTs > 0
            ? Math.Round(Stopwatch.GetElapsedTime(finishTs, firstReadTs).TotalMilliseconds, 3)
            : null;

        return (qWait, proc, deliv);
    }

    public void ResetMeasurementWindow(int currentQueueLength)
    {
        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            _windowStartTimestamp = now;
            _lastQueueChangeTimestamp = now;
            _currentQueueLength = currentQueueLength;
            _weightedQueueIntegralSec = 0.0;
            _windowArrivalsCount = 0;
            _windowQueueWaitSamplesMs.Clear();
            _windowQueueSnapshots.Clear();
            _windowQueueSnapshots.Add((now, currentQueueLength));
            _independentPeriodicQueueSamples.Clear();
            _independentPeriodicQueueSamples.Add(currentQueueLength);
            _windowStarted = true;
        }
    }

    public void ResetMetrics(int currentQueueLength = 0)
    {
        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            _windowStartTimestamp = now;
            _lastQueueChangeTimestamp = now;
            _currentQueueLength = currentQueueLength;
            _weightedQueueIntegralSec = 0.0;
            _windowArrivalsCount = 0;
            _windowQueueWaitSamplesMs.Clear();
            _windowQueueSnapshots.Clear();
            _independentPeriodicQueueSamples.Clear();
            _windowStarted = false;
        }

        _trackedItems.Clear();
        while (_queueWaitSamplesMs.TryDequeue(out _)) { }
        while (_processingTimeSamplesMs.TryDequeue(out _)) { }
        while (_deliveryWaitSamplesMs.TryDequeue(out _)) { }
        while (_totalExecutionSamplesMs.TryDequeue(out _)) { }

        Interlocked.Exchange(ref _totalAccepted, 0);
        Interlocked.Exchange(ref _totalDone, 0);
        Interlocked.Exchange(ref _totalRejected, 0);
        Interlocked.Exchange(ref _totalFailed, 0);
        Interlocked.Exchange(ref _total429, 0);
        Interlocked.Exchange(ref _monotonicOrderViolations, 0);
    }

    public object BuildMetricsSnapshot(int currentQueueLength, ServiceOptions options, bool isShuttingDown)
    {
        var qWaitStats = ComputePercentiles(_queueWaitSamplesMs.ToArray());
        var procStats = ComputePercentiles(_processingTimeSamplesMs.ToArray());
        var delivStats = ComputePercentiles(_deliveryWaitSamplesMs.ToArray());
        var totalStats = ComputePercentiles(_totalExecutionSamplesMs.ToArray());

        double observationWindowSec;
        long arrivalsCount;
        double lambdaRps;
        double avgWaitSec;
        double theoreticalL;
        double observedTimeWeightedL;
        double exactDiscrepancyPct;
        double independentSampledL;
        int independentSampleCount;
        double independentDiscrepancyPct;
        int maxQueueObserved = currentQueueLength;
        bool queueIsBounded = true;

        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_windowStarted && now > _lastQueueChangeTimestamp)
            {
                double dtSec = Stopwatch.GetElapsedTime(_lastQueueChangeTimestamp, now).TotalSeconds;
                _weightedQueueIntegralSec += _currentQueueLength * dtSec;
                _lastQueueChangeTimestamp = now;
            }

            _currentQueueLength = currentQueueLength;
            arrivalsCount = _windowArrivalsCount;

            observationWindowSec = _windowStarted
                ? Stopwatch.GetElapsedTime(_windowStartTimestamp, now).TotalSeconds
                : 0.0;

            lambdaRps = observationWindowSec > 1e-6
                ? arrivalsCount / observationWindowSec
                : 0.0;

            avgWaitSec = _windowQueueWaitSamplesMs.Count > 0
                ? (_windowQueueWaitSamplesMs.Average() / 1000.0)
                : 0.0;

            theoreticalL = lambdaRps * avgWaitSec;

            observedTimeWeightedL = observationWindowSec > 1e-6
                ? (_weightedQueueIntegralSec / observationWindowSec)
                : currentQueueLength;

            exactDiscrepancyPct = theoreticalL > 0.001
                ? Math.Abs(observedTimeWeightedL - theoreticalL) / theoreticalL * 100.0
                : 0.0;

            independentSampleCount = _independentPeriodicQueueSamples.Count;
            independentSampledL = independentSampleCount > 0
                ? _independentPeriodicQueueSamples.Average()
                : observedTimeWeightedL;

            independentDiscrepancyPct = theoreticalL > 0.001
                ? Math.Abs(independentSampledL - theoreticalL) / theoreticalL * 100.0
                : 0.0;

            if (_windowQueueSnapshots.Count > 0)
            {
                maxQueueObserved = _windowQueueSnapshots.Max(s => s.QueueLength);
                queueIsBounded = maxQueueObserved < options.QueueCapacity;
            }
        }

        return new
        {
            service_state = new
            {
                instance_id = options.InstanceId,
                execution_model = options.ExecutionModel,
                queue_engine = options.QueueEngine,
                queue_capacity = options.QueueCapacity,
                workers = options.Workers,
                unsafe_mode = options.UnsafeMode,
                allow_test_overrides = options.AllowTestOverrides,
                is_shutting_down = isShuttingDown
            },
            queue_length = currentQueueLength,
            in_processing = Interlocked.Read(ref _inProcessingCount),
            counters = new
            {
                total_accepted = Interlocked.Read(ref _totalAccepted),
                total_done = Interlocked.Read(ref _totalDone),
                total_rejected = Interlocked.Read(ref _totalRejected),
                total_failed = Interlocked.Read(ref _totalFailed),
                total_429_too_many_requests = Interlocked.Read(ref _total429),
                monotonic_order_violations = Interlocked.Read(ref _monotonicOrderViolations)
            },
            intervals_ms = new
            {
                queue_wait_ms = new
                {
                    description = "Інтервал від прийняття запиту у чергу (POST 202) до початку обробки воркером",
                    sample_count = qWaitStats.Count,
                    mean_ms = qWaitStats.Mean,
                    p50_ms = qWaitStats.P50,
                    p95_ms = qWaitStats.P95,
                    p99_ms = qWaitStats.P99
                },
                processing_time_ms = new
                {
                    description = "Інтервал активної обробки воркером (повільний виклик деканату + транзакція БД)",
                    sample_count = procStats.Count,
                    mean_ms = procStats.Mean,
                    p50_ms = procStats.P50,
                    p95_ms = procStats.P95,
                    p99_ms = procStats.P99
                },
                delivery_wait_ms = new
                {
                    description = "Інтервал від завершення обробки воркером до першого читання результату клієнтом через GET",
                    sample_count = delivStats.Count,
                    mean_ms = delivStats.Mean,
                    p50_ms = delivStats.P50,
                    p95_ms = delivStats.P95,
                    p99_ms = delivStats.P99
                },
                full_domain_execution_ms = new
                {
                    description = "Повний час виконання запиту області (очікування в черзі + обробка воркером)",
                    sample_count = totalStats.Count,
                    mean_ms = totalStats.Mean,
                    p50_ms = totalStats.P50,
                    p95_ms = totalStats.P95,
                    p99_ms = totalStats.P99
                }
            },
            littles_law = new
            {
                formula = "L = lambda * W (continuous time-weighted integral + independent 100ms periodic queue_length sampler)",
                observation_window_sec = Math.Round(observationWindowSec, 3),
                window_arrivals_count = arrivalsCount,
                lambda_arrival_rate_rps = Math.Round(lambdaRps, 3),
                w_avg_queue_wait_sec = Math.Round(avgWaitSec, 4),
                l_theoretical_avg_queue_length = Math.Round(theoreticalL, 4),
                l_observed_avg_queue_length = Math.Round(observedTimeWeightedL, 4),
                relative_discrepancy_pct = Math.Round(exactDiscrepancyPct, 2),
                l_observed_independent_sampled = Math.Round(independentSampledL, 4),
                independent_sample_count = independentSampleCount,
                independent_sampling_interval_ms = 100,
                independent_discrepancy_pct = Math.Round(independentDiscrepancyPct, 2),
                max_queue_length_observed = maxQueueObserved,
                queue_is_bounded = queueIsBounded
            }
        };
    }

    public string BuildPrometheusText(int currentQueueLength)
    {
        var qWait = ComputePercentiles(_queueWaitSamplesMs.ToArray());
        var proc = ComputePercentiles(_processingTimeSamplesMs.ToArray());
        var sb = new StringBuilder();

        sb.AppendLine("# HELP dpc_queue_length Поточна довжина черги запитів");
        sb.AppendLine("# TYPE dpc_queue_length gauge");
        sb.AppendLine($"dpc_queue_length {currentQueueLength}");

        sb.AppendLine("# HELP dpc_in_processing Кількість запитів в обробленні");
        sb.AppendLine("# TYPE dpc_in_processing gauge");
        sb.AppendLine($"dpc_in_processing {Interlocked.Read(ref _inProcessingCount)}");

        sb.AppendLine("# HELP dpc_requests_total Загальна кількість запитів за статусами");
        sb.AppendLine("# TYPE dpc_requests_total counter");
        sb.AppendLine($"dpc_requests_total{{status=\"accepted\"}} {Interlocked.Read(ref _totalAccepted)}");
        sb.AppendLine($"dpc_requests_total{{status=\"done\"}} {Interlocked.Read(ref _totalDone)}");
        sb.AppendLine($"dpc_requests_total{{status=\"rejected\"}} {Interlocked.Read(ref _totalRejected)}");
        sb.AppendLine($"dpc_requests_total{{status=\"failed\"}} {Interlocked.Read(ref _totalFailed)}");
        sb.AppendLine($"dpc_requests_total{{status=\"http_429\"}} {Interlocked.Read(ref _total429)}");

        sb.AppendLine("# HELP dpc_queue_wait_ms Перцентилі часу очікування в черзі (мс)");
        sb.AppendLine("# TYPE dpc_queue_wait_ms gauge");
        sb.AppendLine($"dpc_queue_wait_ms{{quantile=\"0.5\"}} {qWait.P50.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"dpc_queue_wait_ms{{quantile=\"0.95\"}} {qWait.P95.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"dpc_queue_wait_ms{{quantile=\"0.99\"}} {qWait.P99.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        sb.AppendLine("# HELP dpc_processing_time_ms Перцентилі часу обробки воркером (мс)");
        sb.AppendLine("# TYPE dpc_processing_time_ms gauge");
        sb.AppendLine($"dpc_processing_time_ms{{quantile=\"0.5\"}} {proc.P50.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"dpc_processing_time_ms{{quantile=\"0.95\"}} {proc.P95.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"dpc_processing_time_ms{{quantile=\"0.99\"}} {proc.P99.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return sb.ToString();
    }

    private static (int Count, double Mean, double P50, double P95, double P99) ComputePercentiles(double[] samples)
    {
        if (samples.Length == 0)
        {
            return (0, 0.0, 0.0, 0.0, 0.0);
        }

        Array.Sort(samples);
        double mean = samples.Average();
        return (
            samples.Length,
            Math.Round(mean, 3),
            Math.Round(GetPercentile(samples, 0.50), 3),
            Math.Round(GetPercentile(samples, 0.95), 3),
            Math.Round(GetPercentile(samples, 0.99), 3)
        );
    }

    private static double GetPercentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0.0;
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }
}
