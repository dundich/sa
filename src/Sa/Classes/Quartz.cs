using System.Diagnostics;

namespace Sa.Classes;

public static partial class Quartz
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
