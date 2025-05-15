using Sa.Extensions;
using System.Diagnostics;

namespace Sa.Classes;


public static class Retry
{
    /// <summary>
    /// For example: 500ms, 500ms, 500ms ...
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Constant<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int waitTime = 500,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateConstant(TimeSpan.FromMilliseconds(waitTime), retryCount, fastFirst: true)
            .WaitAndRetry(fun, input, next, cancellationToken: cancellationToken);
    }

    [DebuggerStepThrough]
    public static ValueTask<T> Constant<T>(
    Func<CancellationToken, ValueTask<T>> fun,
    int retryCount = 3,
    int waitTime = 500,
    Func<Exception, int, bool>? next = null,
    CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateConstant(TimeSpan.FromMilliseconds(waitTime), retryCount, fastFirst: true)
            .WaitAndRetry(fun, next, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// For example: 100ms, 200ms, 400ms, 800ms, ...
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Exponential<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int initialDelay = 100,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateExponential(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, input, next, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// For example: 100ms, 200ms, 400ms, 800ms, ...
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Exponential<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 100,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateExponential(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, next, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// For example: 100ms, 200ms, 300ms, 400ms, ..
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Linear<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int initialDelay = 100,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateLinear(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, input, next, cancellationToken);
    }


    /// <summary>
    /// For example: 100ms, 200ms, 300ms, 400ms, ..
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Linear<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 100,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateLinear(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, next, cancellationToken);
    }



    /// <summary>
    /// For example: 850ms, 1455ms, 3060ms.
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Jitter<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int initialDelay = 530,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateJitter(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, input, next, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// For example: 850ms, 1455ms, 3060ms.
    /// </summary>
    [DebuggerStepThrough]
    public static ValueTask<T> Jitter<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 530,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Quartz.GenerateJitter(TimeSpan.FromMilliseconds(initialDelay), retryCount, fastFirst: true)
            .WaitAndRetry(fun, next, cancellationToken: cancellationToken);
    }

    [DebuggerStepThrough]
    public static async ValueTask<T> WaitAndRetry<I, T>(
       this IEnumerable<TimeSpan> timeSpans,
       Func<I, CancellationToken, ValueTask<T>> fun,
       I input,
       Func<Exception, int, bool>? next = null,
       CancellationToken cancellationToken = default)
    {
        TimeSpan[] points = [.. timeSpans];

        for (int i = 0; i < points.Length - 1; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                return await fun(input, cancellationToken);
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException)
                {
                    break;
                }
                else if (e.IsCritical() || next != null && !next(e, i))
                {
                    throw;
                }

                await Wait(points[i], cancellationToken);
            }
        }

        if (points.Length > 0)
        {
            await Wait(points[^1], cancellationToken);
        }

        return await fun(input, cancellationToken);
    }

    [DebuggerStepThrough]
    public static async ValueTask<T> WaitAndRetry<T>(
       this IEnumerable<TimeSpan> timeSpans,
       Func<CancellationToken, ValueTask<T>> fun,
       Func<Exception, int, bool>? next = null,
       CancellationToken cancellationToken = default)
    {
        TimeSpan[] points = [.. timeSpans];

        for (int i = 0; i < points.Length - 1; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                return await fun(cancellationToken);
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException)
                {
                    break;
                }
                else if (e.IsCritical() || next != null && !next(e, i))
                {
                    throw;
                }

                await Wait(points[i], cancellationToken);
            }
        }

        if (points.Length > 0)
        {
            await Wait(points[^1], cancellationToken);
        }

        return await fun(cancellationToken);
    }

    private static async Task Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // ignore 
        }
    }


    public static class Quartz
    {
        private static IEnumerable<TimeSpan> Empty() => [];

        private static void ValidateParameters(TimeSpan delay, int retryCount, string delayParamName)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(delayParamName, delay, "should be >= 0ms");
            if (retryCount < 0) throw new ArgumentOutOfRangeException(nameof(retryCount), retryCount, "should be >= 0");
        }

        [DebuggerStepThrough]
        public static IEnumerable<TimeSpan> GenerateConstant(TimeSpan delay, int retryCount, bool fastFirst = false)
        {
            ValidateParameters(delay, retryCount, nameof(delay));
            return retryCount == 0 ? Empty() : Generator.GenConstant(delay, retryCount, fastFirst);
        }

        [DebuggerStepThrough]
        public static IEnumerable<TimeSpan> GenerateLinear(TimeSpan initialDelay, int retryCount, double factor = 1.0, bool fastFirst = true)
        {
            ValidateParameters(initialDelay, retryCount, nameof(initialDelay));
            if (factor < 0) throw new ArgumentOutOfRangeException(nameof(factor), factor, "should be >= 0");

            return retryCount == 0 ? Empty() : Generator.GenLinear(initialDelay, retryCount, factor, fastFirst);
        }

        [DebuggerStepThrough]
        public static IEnumerable<TimeSpan> GenerateExponential(TimeSpan initialDelay, int retryCount, double factor = 2.0, bool fastFirst = true)
        {
            ValidateParameters(initialDelay, retryCount, nameof(initialDelay));
            if (factor < 1.0) throw new ArgumentOutOfRangeException(nameof(factor), factor, "should be >= 1.0");

            return retryCount == 0 ? Empty() : Generator.GenExponential(initialDelay, retryCount, factor, fastFirst);
        }

        [DebuggerStepThrough]
        public static IEnumerable<TimeSpan> GenerateJitter(TimeSpan medianFirstRetryDelay, int retryCount, bool fastFirst = true)
        {
            ValidateParameters(medianFirstRetryDelay, retryCount, nameof(medianFirstRetryDelay));
            return retryCount == 0 ? Empty() : Generator.GenJitter(medianFirstRetryDelay, retryCount, fastFirst);
        }


        static class Generator
        {

            public static IEnumerable<TimeSpan> GenConstant(TimeSpan delay, int retryCount, bool fastFirst)
            {
                if (fastFirst)
                {
                    yield return TimeSpan.Zero;
                }

                for (int i = fastFirst ? 1 : 0; i < retryCount; i++)
                {
                    yield return delay;
                }
            }

            public static IEnumerable<TimeSpan> GenLinear(TimeSpan initialDelay, int retryCount, double factor, bool fastFirst)
            {
                if (fastFirst)
                {
                    yield return TimeSpan.Zero;
                }

                double ms = initialDelay.TotalMilliseconds;
                double increment = factor * ms;

                for (int i = fastFirst ? 1 : 0; i < retryCount; i++, ms += increment)
                {
                    yield return TimeSpan.FromMilliseconds(ms);
                }
            }

            public static IEnumerable<TimeSpan> GenExponential(TimeSpan initialDelay, int retryCount, double factor, bool fastFirst)
            {
                if (fastFirst)
                {
                    yield return TimeSpan.Zero;
                }

                double ms = initialDelay.TotalMilliseconds;

                for (int i = fastFirst ? 1 : 0; i < retryCount; i++, ms *= factor)
                {
                    yield return TimeSpan.FromMilliseconds(ms);
                }
            }

            public static IEnumerable<TimeSpan> GenJitter(TimeSpan medianFirstRetryDelay, int retryCount, bool fastFirst)
            {
                const double pFactor = 4.0;
                const double rpScalingFactor = 1 / 1.4d;
                double maxTimeSpanDouble = (double)TimeSpan.MaxValue.Ticks - 1000;

                if (fastFirst)
                {
                    yield return TimeSpan.Zero;
                }

                long targetTicksFirstDelay = medianFirstRetryDelay.Ticks;
                double prev = 0.0;

                for (int i = fastFirst ? 1 : 0; i < retryCount; i++)
                {
                    double t = i + Random.Shared.NextDouble();
                    double next = Math.Pow(2, t) * Math.Tanh(Math.Sqrt(pFactor * t));
                    double formulaIntrinsicValue = next - prev;

                    yield return TimeSpan.FromTicks((long)Math.Min(formulaIntrinsicValue * rpScalingFactor * targetTicksFirstDelay, maxTimeSpanDouble));
                    prev = next;
                }
            }
        }
    }
}