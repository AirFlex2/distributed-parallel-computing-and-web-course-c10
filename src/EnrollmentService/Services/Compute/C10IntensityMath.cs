using System.Runtime.CompilerServices;

namespace EnrollmentService.Services.Compute;

// Математична модель теми C10 («Запис на вибіркові дисципліни») для ЛР2 РПО (О2.1–О2.4):
// Інтенсивність потоку заявок: λ(t) = B + A · exp(-t / τ), t ∈ [0, T]
// Первісна: F(t) = B · t - A · τ · exp(-t / τ)
// Аналітичний інтеграл на [a, b]: I(a, b) = F(b) - F(a) = B · (b - a) + A · τ · (exp(-a / τ) - exp(-b / τ))
public static class C10IntensityMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EvaluateLambda(double t, double modelA, double modelB, double modelTau)
    {
        return modelB + modelA * Math.Exp(-t / modelTau);
    }

    // Неінлайнений виклик функції інтенсивності λ(t) для початкового (неоптимізованого) варіанта О2.3,
    // щоб семплюючий профайлер стека (dotnet-trace) явно відображав частку процесорного часу у викликах EvaluateLambdaNaive / Math.Exp.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double EvaluateLambdaNaive(double t, double modelA, double modelB, double modelTau)
    {
        return modelB + modelA * Math.Exp(-t / modelTau);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double EvaluateAntiderivative(double t, double modelA, double modelB, double modelTau)
    {
        return modelB * t - modelA * modelTau * Math.Exp(-t / modelTau);
    }

    public static double ComputeAnalyticalIntegral(double a, double b, double modelA, double modelB, double modelTau)
    {
        if (a == b)
        {
            return 0.0;
        }

        return modelB * (b - a) + modelA * modelTau * (Math.Exp(-a / modelTau) - Math.Exp(-b / modelTau));
    }

    // О2.1: Розбиття діапазону [0, n] між p = workers процесами за цілими індексами без пропусків і перекриттів
    public static (long StartIndex, long EndIndex) GetWorkerSubintervalBounds(long n, int workers, int workerIndex)
    {
        if (n < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "Кількість підінтервалів n має бути >= 1.");
        }
        if (workers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(workers), "Кількість працівників має бути >= 1.");
        }
        if (workerIndex < 0 || workerIndex >= workers)
        {
            throw new ArgumentOutOfRangeException(nameof(workerIndex), "Індекс працівника поза межами [0, workers - 1].");
        }

        // Використовуємо Int128 для запобігання переповненню при великих n (наприклад, n >= 10^9)
        long start = (long)((Int128)workerIndex * n / workers);
        long end = (long)((Int128)(workerIndex + 1) * n / workers);
        return (start, end);
    }

    // О2.1 та О2.4 (mode = "local"): Обчислення часткової суми методу трапецій на піддіапазоні [startIdx, endIdx]
    // у природному порядку індексів зліва направо (order = "natural").
    public static double ComputeTrapezoidSliceLocal(
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
            return 0.0;
        }

        double h = (b - a) / n;
        double invNegTau = -1.0 / modelTau;

        double tStart = a + startIdx * h;
        double tEnd = (endIdx == n) ? b : (a + endIdx * h);

        double sum = 0.5 * (modelB + modelA * Math.Exp(tStart * invNegTau));

        for (long k = startIdx + 1; k < endIdx; k++)
        {
            double t = a + k * h;
            sum += modelB + modelA * Math.Exp(t * invNegTau);
        }

        sum += 0.5 * (modelB + modelA * Math.Exp(tEnd * invNegTau));
        return sum * h;
    }

    // Допоміжна реалізація наївного поелементного обчислення трапецій (2 виклики Math.Exp на кожен підінтервал)
    // для експериментальної перевірки гіпотези оптимізації в О2.3.
    public static double ComputeTrapezoidSliceNaivePerSubinterval(
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
            return 0.0;
        }

        double h = (b - a) / n;
        double halfH = 0.5 * h;
        double total = 0.0;

        for (long k = startIdx; k < endIdx; k++)
        {
            double t0 = a + k * h;
            double t1 = a + (k + 1) * h;
            double f0 = EvaluateLambdaNaive(t0, modelA, modelB, modelTau);
            double f1 = EvaluateLambdaNaive(t1, modelA, modelB, modelTau);
            total += (f0 + f1) * halfH;
        }

        return total;
    }
}
