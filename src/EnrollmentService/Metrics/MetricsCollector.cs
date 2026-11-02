using System.Collections.Concurrent;
using System.Diagnostics;
using EnrollmentService.Queues;

namespace EnrollmentService.Metrics;

// ============================================================================
// ПУНКТ О1.5 (Час виконання й метрики) та ПУНКТ Д1.2 (Перевірка закону Літтла)
// Містить точний часово-зважений інтеграл черги та незалежне періодичне
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
    // Накопичувальні суми та лічильники без обмеження у 500 замірів для коректності на інтервалах > 50 с
    private bool _windowStarted;
    private long _windowStartTimestamp = Stopwatch.GetTimestamp();
    private long _lastQueueChangeTimestamp = Stopwatch.GetTimestamp();
    private int _currentQueueLength;
    private double _weightedQueueIntegralSec;
    private long _windowArrivalsCount;
    private double _windowQueueWaitSumMs;
    private long _windowQueueWaitCount;
    private long _independentSampleSum;
    private long _independentSampleCount;
    private int _windowMaxQueueObserved;

    private static void AddBoundedSample(ConcurrentQueue<double> q, double val, int max = 500)
    {
        q.Enqueue(val);
        while (q.Count > max && q.TryDequeue(out _)) { }
    }

    public bool TryEnqueueReservedAndRecordAccepted(IBoundedRequestQueue queue, QueuedRequestItem item)
    {
        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (item.EnqueueTimestamp == 0)
            {
                item.EnqueueTimestamp = now;
            }

            if (!queue.EnqueueReserved(item))
            {
                return false;
            }

            RecordAcceptedLocked(item, now, queue.Count);
            return true;
        }
    }

    public bool TryEnqueueAndRecordAccepted(IBoundedRequestQueue queue, QueuedRequestItem item)
    {
        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (item.EnqueueTimestamp == 0)
            {
                item.EnqueueTimestamp = now;
            }

            if (!queue.TryEnqueue(item))
            {
                return false;
            }

            RecordAcceptedLocked(item, now, queue.Count);
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
            _windowArrivalsCount = 0;
            _windowQueueWaitSumMs = 0.0;
            _windowQueueWaitCount = 0;
            _independentSampleSum = 0;
            _independentSampleCount = 0;
            _windowMaxQueueObserved = 0;
            _windowStarted = true;
        }
        else if (now > _lastQueueChangeTimestamp)
        {
            double dtSec = Stopwatch.GetElapsedTime(_lastQueueChangeTimestamp, now).TotalSeconds;
            _weightedQueueIntegralSec += _currentQueueLength * dtSec;
            _lastQueueChangeTimestamp = now;
        }

        _currentQueueLength = explicitQueueLength ?? (_currentQueueLength + 1);
        if (_currentQueueLength > _windowMaxQueueObserved)
        {
            _windowMaxQueueObserved = _currentQueueLength;
        }
        _windowArrivalsCount++;
    }

    public void RecordRejected429()
    {
        Interlocked.Increment(ref _total429);
    }

    public void RecordWorkerStarted(QueuedRequestItem item, int currentQueueLength)
    {
        long now = Stopwatch.GetTimestamp();
        item.StartProcessingTimestamp = now;

        Interlocked.Increment(ref _inProcessingCount);

        lock (_stateLock)
        {
            if (_windowStarted && now > _lastQueueChangeTimestamp)
            {
                double dtSec = Stopwatch.GetElapsedTime(_lastQueueChangeTimestamp, now).TotalSeconds;
                _weightedQueueIntegralSec += _currentQueueLength * dtSec;
                _lastQueueChangeTimestamp = now;
            }

            _currentQueueLength = currentQueueLength;
            if (_currentQueueLength > _windowMaxQueueObserved)
            {
                _windowMaxQueueObserved = _currentQueueLength;
            }

            if (item.EnqueueTimestamp > 0 && now >= item.EnqueueTimestamp)
            {
                double waitMs = Stopwatch.GetElapsedTime(item.EnqueueTimestamp, now).TotalMilliseconds;
                AddBoundedSample(_queueWaitSamplesMs, waitMs);
                _windowQueueWaitSumMs += waitMs;
                _windowQueueWaitCount++;
            }
        }
    }

    public DateTimeOffset MarkProcessingFinished(QueuedRequestItem item)
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref item.FinishProcessingTimestamp, now, 0);
        TryRecordDeliverySample(item);
        return DateTimeOffset.UtcNow;
    }

    public void ResetProcessingFinished(QueuedRequestItem item)
    {
        Interlocked.Exchange(ref item.FinishProcessingTimestamp, 0);
        Interlocked.Exchange(ref item.FirstReadTimestamp, 0);
        Interlocked.Exchange(ref item.DeliverySampleRecorded, 0);
    }

    public void RecordWorkerFinished(QueuedRequestItem item, string finalStatus, int currentQueueLength)
    {
        long now = item.FinishProcessingTimestamp > 0 ? item.FinishProcessingTimestamp : Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref item.FinishProcessingTimestamp, now, 0);
        TryRecordDeliverySample(item);

        Interlocked.Decrement(ref _inProcessingCount);

        switch (finalStatus)
        {
            case "done":
                Interlocked.Increment(ref _totalDone);
                break;
            case "rejected":
                Interlocked.Increment(ref _totalRejected);
                break;
            default:
                Interlocked.Increment(ref _totalFailed);
                break;
        }

        if (item.StartProcessingTimestamp > 0 && now >= item.StartProcessingTimestamp)
        {
            double procMs = Stopwatch.GetElapsedTime(item.StartProcessingTimestamp, now).TotalMilliseconds;
            AddBoundedSample(_processingTimeSamplesMs, procMs);
        }

        if (item.EnqueueTimestamp > 0 && now >= item.EnqueueTimestamp)
        {
            double fullMs = Stopwatch.GetElapsedTime(item.EnqueueTimestamp, now).TotalMilliseconds;
            AddBoundedSample(_totalExecutionSamplesMs, fullMs);
        }

        if (item.EnqueueTimestamp > 0 && item.StartProcessingTimestamp > 0 && item.FinishProcessingTimestamp > 0)
        {
            if (item.EnqueueTimestamp > item.StartProcessingTimestamp || item.StartProcessingTimestamp > item.FinishProcessingTimestamp)
            {
                Interlocked.Increment(ref _monotonicOrderViolations);
            }
        }

        // Обмеження пам'яті для довготривалих навантажувальних тестів (О1.5): ліміт 2000 записів
        PruneTrackedItemsIfNeeded();
    }

    public void RecordFirstRead(Guid id) => RecordFirstReadIfCompleted(id);

    public void RecordFirstReadIfCompleted(Guid id)
    {
        if (!_trackedItems.TryGetValue(id, out var item))
        {
            return;
        }

        // О1.5 DPC: час першої видачі кінцевого результату фіксується ТІЛЬКИ для вже завершених заявок (finish > 0).
        // Ранній опрос (polling) до завершення не повинен фіксувати FirstReadTimestamp
        // та не повинен обнуляти delivery_wait_ms.
        if (Volatile.Read(ref item.FinishProcessingTimestamp) == 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (Interlocked.CompareExchange(ref item.FirstReadTimestamp, now, 0) == 0)
        {
            TryRecordDeliverySample(item);
        }
    }

    private void TryRecordDeliverySample(QueuedRequestItem item)
    {
        long finish = Volatile.Read(ref item.FinishProcessingTimestamp);
        long firstRead = Volatile.Read(ref item.FirstReadTimestamp);

        if (finish > 0 && firstRead > 0)
        {
            if (Interlocked.Exchange(ref item.DeliverySampleRecorded, 1) == 0)
            {
                if (finish > firstRead)
                {
                    // Клієнт прочитав статус 'done' з БД миттєво після транзакції до взяття мітки часу finish воркером.
                    // Вирівнюємо час видачі до 0.0 мс, щоб уникнути викривлення монотонності.
                    finish = firstRead;
                }

                double deliveryMs = Stopwatch.GetElapsedTime(finish, firstRead).TotalMilliseconds;
                AddBoundedSample(_deliveryWaitSamplesMs, deliveryMs);
            }
        }
    }

    public void RecordIndependentPeriodicSample(int queueLength)
    {
        lock (_stateLock)
        {
            _independentSampleSum += queueLength;
            _independentSampleCount++;
            if (queueLength > _windowMaxQueueObserved)
            {
                _windowMaxQueueObserved = queueLength;
            }
        }
    }

    public (double? QueueWaitMs, double? ProcessingMs, double? DeliveryWaitMs) GetItemTimings(Guid id)
    {
        if (!_trackedItems.TryGetValue(id, out var item))
        {
            return (null, null, null);
        }

        double? qWait = (item.EnqueueTimestamp > 0 && item.StartProcessingTimestamp > 0)
            ? Math.Round(Stopwatch.GetElapsedTime(item.EnqueueTimestamp, item.StartProcessingTimestamp).TotalMilliseconds, 3)
            : null;

        double? proc = (item.StartProcessingTimestamp > 0 && item.FinishProcessingTimestamp > 0)
            ? Math.Round(Stopwatch.GetElapsedTime(item.StartProcessingTimestamp, item.FinishProcessingTimestamp).TotalMilliseconds, 3)
            : null;

        double? deliv = (item.FinishProcessingTimestamp > 0 && item.FirstReadTimestamp > 0)
            ? (item.FirstReadTimestamp >= item.FinishProcessingTimestamp
                ? Math.Round(Stopwatch.GetElapsedTime(item.FinishProcessingTimestamp, item.FirstReadTimestamp).TotalMilliseconds, 3)
                : 0.0)
            : null;

        // Прунінг завершених елементів для уникнення витоку пам'яті (О1.5)
        PruneTrackedItemsIfNeeded();

        return (qWait, proc, deliv);
    }

    private void PruneTrackedItemsIfNeeded()
    {
        if (_trackedItems.Count <= 2000)
        {
            return;
        }

        // 1. Спочатку видаляємо елементи, які вже завершені та прочитані клієнтом
        var readKeys = _trackedItems
            .Where(kvp => kvp.Value.FirstReadTimestamp > 0 && kvp.Value.FinishProcessingTimestamp > 0)
            .Select(kvp => kvp.Key)
            .Take(500)
            .ToList();

        foreach (var key in readKeys)
        {
            _trackedItems.TryRemove(key, out _);
        }

        // 2. Якщо кількість все ще перевищує ліміт 2000, видаляємо найстаріші завершені заявки
        if (_trackedItems.Count > 2000)
        {
            var finishedKeys = _trackedItems
                .Where(kvp => kvp.Value.FinishProcessingTimestamp > 0)
                .OrderBy(kvp => kvp.Value.EnqueueTimestamp)
                .Select(kvp => kvp.Key)
                .Take(500)
                .ToList();

            foreach (var key in finishedKeys)
            {
                _trackedItems.TryRemove(key, out _);
            }
        }

        // 3. Якщо через екстремальний потік незавершених елементів перевищено 2500, видаляємо найстаріші за EnqueueTimestamp
        if (_trackedItems.Count > 2500)
        {
            var oldestKeys = _trackedItems
                .OrderBy(kvp => kvp.Value.EnqueueTimestamp)
                .Select(kvp => kvp.Key)
                .Take(500)
                .ToList();

            foreach (var key in oldestKeys)
            {
                _trackedItems.TryRemove(key, out _);
            }
        }
    }

    public void ResetMetrics(int currentQueueLength = 0)
    {
        lock (_stateLock)
        {
            Interlocked.Exchange(ref _inProcessingCount, 0);
            Interlocked.Exchange(ref _totalAccepted, 0);
            Interlocked.Exchange(ref _totalDone, 0);
            Interlocked.Exchange(ref _totalRejected, 0);
            Interlocked.Exchange(ref _totalFailed, 0);
            Interlocked.Exchange(ref _total429, 0);
            Interlocked.Exchange(ref _monotonicOrderViolations, 0);

            _trackedItems.Clear();
            _queueWaitSamplesMs.Clear();
            _processingTimeSamplesMs.Clear();
            _deliveryWaitSamplesMs.Clear();
            _totalExecutionSamplesMs.Clear();

            ResetMeasurementWindow(currentQueueLength);
        }
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
            _windowQueueWaitSumMs = 0.0;
            _windowQueueWaitCount = 0;
            _independentSampleSum = 0;
            _independentSampleCount = 0;
            _windowMaxQueueObserved = currentQueueLength;
            _windowStarted = true;
        }
    }

    public object BuildMetricsSnapshot(int currentQueueLength, ServiceOptions options, bool isShuttingDown)
    {
        long now = Stopwatch.GetTimestamp();

        double observationWindowSec;
        long arrivalsCount;
        double observedTimeWeightedL;
        double avgWaitSec = 0.0;
        double lambdaRps = 0.0;
        double theoreticalL = 0.0;
        double exactDiscrepancyPct = 0.0;
        double independentSampledL = 0.0;
        int independentSampleCount = 0;
        double independentDiscrepancyPct = 0.0;
        int maxQueueObserved;
        bool queueIsBounded;

        lock (_stateLock)
        {
            observationWindowSec = _windowStarted
                ? Math.Max(0.001, Stopwatch.GetElapsedTime(_windowStartTimestamp, now).TotalSeconds)
                : 0.001;

            double currentIntegral = _weightedQueueIntegralSec;
            if (_windowStarted && now > _lastQueueChangeTimestamp)
            {
                currentIntegral += _currentQueueLength * Stopwatch.GetElapsedTime(_lastQueueChangeTimestamp, now).TotalSeconds;
            }

            observedTimeWeightedL = currentIntegral / observationWindowSec;
            arrivalsCount = _windowArrivalsCount;

            if (_windowQueueWaitCount > 0)
            {
                avgWaitSec = (_windowQueueWaitSumMs / _windowQueueWaitCount) / 1000.0;
            }

            lambdaRps = arrivalsCount / observationWindowSec;
            theoreticalL = lambdaRps * avgWaitSec;

            if (observedTimeWeightedL > 1e-4 && theoreticalL > 1e-4)
            {
                exactDiscrepancyPct = Math.Abs(observedTimeWeightedL - theoreticalL) / Math.Max(observedTimeWeightedL, theoreticalL) * 100.0;
            }

            independentSampleCount = (int)Math.Min(int.MaxValue, _independentSampleCount);
            if (_independentSampleCount > 0)
            {
                independentSampledL = (double)_independentSampleSum / _independentSampleCount;
                if (independentSampledL > 1e-4 && theoreticalL > 1e-4)
                {
                    independentDiscrepancyPct = Math.Abs(independentSampledL - theoreticalL) / Math.Max(independentSampledL, theoreticalL) * 100.0;
                }
            }

            maxQueueObserved = Math.Max(_windowMaxQueueObserved, currentQueueLength);
            queueIsBounded = maxQueueObserved <= options.QueueCapacity;
        }

        var qWaitStats = ComputePercentiles(_queueWaitSamplesMs.ToArray());
        var procStats = ComputePercentiles(_processingTimeSamplesMs.ToArray());
        var delivStats = ComputePercentiles(_deliveryWaitSamplesMs.ToArray());
        var totalStats = ComputePercentiles(_totalExecutionSamplesMs.ToArray());

        return new
        {
            service = "C10 Enrollment Service",
            instance_id = options.InstanceId,
            timestamp_utc = DateTimeOffset.UtcNow,
            configuration = new
            {
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
                    description = "Інтервал від прийняття запиту у чергу (POST 202) до початку обробки воркером (вибірка: останні до 500 замірів)",
                    sample_count = qWaitStats.Count,
                    mean_ms = qWaitStats.Mean,
                    p50_ms = qWaitStats.P50,
                    p95_ms = qWaitStats.P95,
                    p99_ms = qWaitStats.P99
                },
                processing_time_ms = new
                {
                    description = "Інтервал активної обробки воркером (повільний виклик деканату + транзакція БД, вибірка: останні до 500 замірів)",
                    sample_count = procStats.Count,
                    mean_ms = procStats.Mean,
                    p50_ms = procStats.P50,
                    p95_ms = procStats.P95,
                    p99_ms = procStats.P99
                },
                delivery_wait_ms = new
                {
                    description = "Інтервал від завершення обробки воркером до першого читання результату клієнтом через GET (вибірка: останні до 500 замірів)",
                    sample_count = delivStats.Count,
                    mean_ms = delivStats.Mean,
                    p50_ms = delivStats.P50,
                    p95_ms = delivStats.P95,
                    p99_ms = delivStats.P99
                },
                full_domain_execution_ms = new
                {
                    description = "Повний час виконання запиту: очікування в черзі + обробка воркером (вибірка: останні до 500 замірів)",
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
