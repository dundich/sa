using System.Diagnostics;

namespace Sa.Classes;

/// <summary>
/// Provides retry strategy helpers: constant, linear, exponential, and decorrelated jitter back-off.
/// Mirrors the Polly library patterns with a lightweight, allocation-minimal implementation targeting .NET 10 AOT.
/// </summary>
internal static class Retry
{
    #region Strategy entry points

    /// <summary>
    /// Executes <paramref name="fun"/> with constant (fixed-delay) retries (<paramref name="input"/> variant).
    /// </summary>
    /// <remarks>
    /// First retry has zero delay ("fast-first"). Subsequent retries use the fixed <paramref name="waitTime"/>.
    /// </remarks>
    /// <example>
    /// Delays: 0 ms (fast-first), 500 ms, 500 ms, 500 ms …
    /// </example>
    [DebuggerStepThrough]
    public static ValueTask<T> Constant<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int waitTime = 500,
        Func<Exception, int, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        return WaitAndRetry(Quartz.GenerateConstant(TimeSpan.FromMilliseconds(waitTime), retryCount, fastFirst: true),
            fun, input, shouldRetry, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="fun"/> with constant (fixed-delay) retries (no input parameter).
    /// </summary>
    /// <remarks>
    /// First retry has zero delay ("fast-first"). Subsequent retries use the fixed <paramref name="waitTime"/>.
    /// </remarks>
    /// <example>
    /// Delays: 0 ms (fast-first), 500 ms, 500 ms, 500 ms …
    /// </example>
    [DebuggerStepThrough]
    public static ValueTask<T> Constant<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int waitTime = 500,
        Func<Exception, int, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        return WaitAndRetry(Quartz.GenerateConstant(TimeSpan.FromMilliseconds(waitTime), retryCount, fastFirst: true),
            fun, shouldRetry, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="fun"/> with exponential back-off retries (<paramref name="input"/> variant).
    /// </summary>
    /// <remarks>
    /// First retry has zero delay ("fast-first"). Subsequent delays grow as <c>initialDelay × factorⁱ</c>.
    /// </remarks>
    /// <example>
    /// Delays: 0 ms (fast-first), 100 ms, 200 ms, 400 ms …
    /// </example>
    [DebuggerStepThrough]
    public static ValueTask<T> Exponential<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int initialDelay = 100,
        double factor = 2.0,
        Func<Exception, int, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        return WaitAndRetry(Quartz.GenerateExponential(TimeSpan.FromMilliseconds(initialDelay), retryCount, factor, fastFirst: true),
            fun, input, shouldRetry, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="fun"/> with exponential back-off retries (no input parameter).
    /// </summary>
    /// <remarks>
    /// First retry has zero delay ("fast-first"). Subsequent delays grow as <c>initialDelay × factorⁱ</c>.
    /// </remarks>
    /// <example>
    /// Delays: 0 ms (fast-first), 100 ms, 200 ms, 400 ms …
    /// </example>
    [DebuggerStepThrough]
    public static ValueTask<T> Exponential<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 100,
        double factor = 2.0,
        Func<Exception, int, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        return WaitAndRetry(Quartz.GenerateExponential(TimeSpan.FromMilliseconds(initialDelay), retryCount, factor, fastFirst: true),
            fun, shouldRetry, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="fun"/> with linear back-off retries (<paramref name="input"/> variant).
    /// </summary>
    /// <remarks>
    /// First retry has zero delay ("fast-first"). Subsequent delays grow linearly: <c>initialDelay × factor × i</c>.
    /// </remarks>
    /// <example>
    /// Delays: 0 ms (fast-first), 100 ms, 200 ms, 300 ms …
    /// </example>
    [DebuggerStepThrough]
    public static ValueTask<T> Linear<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int initialDelay = 100,
        double factor = 1.0,
        Func<Exception, int, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        return WaitAndRetry(Quartz.GenerateLinear(TimeSpan.FromMilliseconds(initialDelay), retryCount, factor, fastFirst: true),
            fun, input, shouldRetry, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="fun"/> with linear back-off retries (no input parameter).
    /// </summary>
    /// <remarks>
    /// First retry has zero delay ("fast-first"). Subsequent delays grow linearly: <c>initialDelay × factor × i</c>.
    /// </remarks>
    /// <example>
    /// Delays: 0 ms (fast-first), 100 ms, 200 ms, 300 ms …
    /// </example>
    [DebuggerStepThrough]
    public static ValueTask<T> Linear<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 100,
        double factor = 1.0,
        Func<Exception, int, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        return WaitAndRetry(Quartz.GenerateLinear(TimeSpan.FromMilliseconds(initialDelay), retryCount, factor, fastFirst: true),
            fun, shouldRetry, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="fun"/> with Microsoft Azure-style decorrelated jitter retries (<paramref name="input"/> variant).
    /// </summary>
    /// <remarks>
    /// Each delay is uniformly sampled between 0 and 3× the median, avoiding thundering herd.
    /// </remarks>
    /// <example>
    /// Delays: 0 ms (fast-first), ~530 ms, ~1455 ms, ~3060 ms …
    /// </example>
    [DebuggerStepThrough]
    public static ValueTask<T> Jitter<I, T>(
        Func<I, CancellationToken, ValueTask<T>> fun,
        I input,
        int retryCount = 3,
        int medianFirstRetryDelay = 530,
        Func<Exception, int, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        return WaitAndRetry(Quartz.GenerateJitter(TimeSpan.FromMilliseconds(medianFirstRetryDelay), retryCount, fastFirst: true),
            fun, input, shouldRetry, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="fun"/> with Microsoft Azure-style decorrelated jitter retries (no input parameter).
    /// </summary>
    /// <remarks>
    /// Each delay is uniformly sampled between 0 and 3× the median, avoiding thundering herd.
    /// </remarks>
    /// <example>
    /// Delays: 0 ms (fast-first), ~530 ms, ~1455 ms, ~3060 ms …
    /// </example>
    [DebuggerStepThrough]
    public static ValueTask<T> Jitter<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int medianFirstRetryDelay = 530,
        Func<Exception, int, bool>? shouldRetry = null,
        CancellationToken cancellationToken = default)
    {
        return WaitAndRetry(Quartz.GenerateJitter(TimeSpan.FromMilliseconds(medianFirstRetryDelay), retryCount, fastFirst: true),
            fun, shouldRetry, cancellationToken);
    }

    #endregion

    #region WaitAndRetry core (shared by all strategies)

    /// <summary>
    /// Retries <paramref name="fun"/> by awaiting each delay in <paramref name="timeSpans"/> on failure.
    /// </summary>
    /// <param name="timeSpans">Sequence of delays produced by a Quartz generator.</param>
    /// <param name="shouldRetry">
    /// Returns <see langword="true"/> to retry after the caught exception. When <see langword="null"/>,
    /// critical exceptions are re-thrown immediately while transient ones are retried.
    /// </param>
    [DebuggerStepThrough]
#pragma warning disable S3776
    public static async ValueTask<T> WaitAndRetry<I, T>(
#pragma warning restore S3776
       IEnumerable<TimeSpan> timeSpans,
       Func<I, CancellationToken, ValueTask<T>> fun,
       I input,
       Func<Exception, int, bool>? shouldRetry = null,
       CancellationToken cancellationToken = default)
    {
        TimeSpan[] points = [.. timeSpans];
        if (points.Length == 0)
            return await fun(input, cancellationToken).ConfigureAwait(false);

        Exception? lastEx = null;
        for (int i = 0; i < points.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                return await fun(input, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                if (e is OperationCanceledException oce && oce.CancellationToken == cancellationToken)
                    throw;

                if (shouldRetry != null && !shouldRetry(e, i))
                    throw;

                lastEx = e;
                if (i < points.Length - 1)
                {
                    await Delay(points[i], cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (lastEx != null)
            throw lastEx;

        throw new OperationCanceledException("Retry loop exited due to cancellation.", null, cancellationToken);
    }

    /// <summary>
    /// Retries <paramref name="fun"/> by awaiting each delay in <paramref name="timeSpans"/> on failure (no input parameter).
    /// </summary>
    [DebuggerStepThrough]
#pragma warning disable S3776
    public static async ValueTask<T> WaitAndRetry<T>(
#pragma warning restore S3776
       IEnumerable<TimeSpan> timeSpans,
       Func<CancellationToken, ValueTask<T>> fun,
       Func<Exception, int, bool>? shouldRetry = null,
       CancellationToken cancellationToken = default)
    {
        TimeSpan[] points = [.. timeSpans];
        if (points.Length == 0)
            return await fun(cancellationToken).ConfigureAwait(false);

        Exception? lastEx = null;
        for (int i = 0; i < points.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                return await fun(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                if (e is OperationCanceledException oce && oce.CancellationToken == cancellationToken)
                    throw;

                if (shouldRetry != null && !shouldRetry(e, i))
                    throw;

                lastEx = e;
                if (i < points.Length - 1)
                {
                    await Delay(points[i], cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (lastEx != null)
            throw lastEx;

        throw new OperationCanceledException("Retry loop exited due to cancellation.", null, cancellationToken);
    }

    #endregion

    #region Private helpers

    [DebuggerStepThrough]
    private static async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Ignored — cancellation already signalled.
        }
    }

    #endregion

    #region Critical-exception detection

    /// <summary>
    /// Determines whether <paramref name="ex"/> is a critical (non-retriable) exception.
    /// </summary>
    [DebuggerStepThrough]
    public static bool IsFatal(Exception ex)
    {
        return ex is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException
            or BadImageFormatException
            or CannotUnloadAppDomainException
            or InvalidProgramException
            or ThreadAbortException;
    }

    #endregion

    #region Quartz generator (delay sequence factories)

    /// <summary>
    /// Generates <see cref="TimeSpan"/> sequences for various retry back-off strategies.
    /// The first element is always <see cref="TimeSpan.Zero"/> when <paramref name="fastFirst"/> is <see langword="true"/>,
    /// enabling an immediate second attempt with zero wait.
    /// </summary>
    public static class Quartz
    {
        private static IEnumerable<TimeSpan> Empty() => [];

        /// <summary>
        /// Validates common parameters for delay generators.
        /// </summary>
        private static void ValidateParameters(TimeSpan delay, int retryCount, string delayParamName)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(delayParamName, delay, "Delay must be ≥ 0.");
            if (retryCount < 0) throw new ArgumentOutOfRangeException(nameof(retryCount), retryCount, "retryCount must be ≥ 0.");
        }

        /// <summary>
        /// Generates a constant-delay sequence: 0, D, D, D, … (fastFirst=True) or D, D, D, … (fastFirst=False).
        /// </summary>
        [DebuggerStepThrough]
        public static IEnumerable<TimeSpan> GenerateConstant(TimeSpan delay, int retryCount, bool fastFirst = false)
        {
            ValidateParameters(delay, retryCount, nameof(delay));
            return retryCount == 0 ? Empty() : Generator.GenConstant(delay, retryCount, fastFirst);
        }

        /// <summary>
        /// Generates a linear-back-off sequence: 0, I, 2I, 3I, … (fastFirst=True) where I = initialDelay × factor.
        /// </summary>
        [DebuggerStepThrough]
        public static IEnumerable<TimeSpan> GenerateLinear(
            TimeSpan initialDelay, int retryCount, double factor = 1.0, bool fastFirst = true)
        {
            ValidateParameters(initialDelay, retryCount, nameof(initialDelay));
            if (factor < 0) throw new ArgumentOutOfRangeException(nameof(factor), factor, "Factor must be ≥ 0.");
            return retryCount == 0 ? Empty() : Generator.GenLinear(initialDelay, retryCount, factor, fastFirst);
        }

        /// <summary>
        /// Generates an exponential-back-off sequence: 0, I, I·F, I·F², … (fastFirst=True) where F = factor.
        /// </summary>
        [DebuggerStepThrough]
        public static IEnumerable<TimeSpan> GenerateExponential(
            TimeSpan initialDelay, int retryCount, double factor = 2.0, bool fastFirst = true)
        {
            ValidateParameters(initialDelay, retryCount, nameof(initialDelay));
            if (factor < 1.0) throw new ArgumentOutOfRangeException(nameof(factor), factor, "Factor must be ≥ 1.0.");
            return retryCount == 0 ? Empty() : Generator.GenExponential(initialDelay, retryCount, factor, fastFirst);
        }

        /// <summary>
        /// Generates an Azure-style decorrelated-jitter sequence using the intrinsic-value formula.
        /// Each delay is sampled uniformly between 0 and 3× the median to prevent thundering herd.
        /// </summary>
        [DebuggerStepThrough]
        public static IEnumerable<TimeSpan> GenerateJitter(
            TimeSpan medianFirstRetryDelay, int retryCount, bool fastFirst = true)
        {
            ValidateParameters(medianFirstRetryDelay, retryCount, nameof(medianFirstRetryDelay));
            return retryCount == 0 ? Empty() : Generator.GenJitter(medianFirstRetryDelay, retryCount, fastFirst);
        }

        #region Generator implementations

        static class Generator
        {
            public static IEnumerable<TimeSpan> GenConstant(TimeSpan delay, int retryCount, bool fastFirst)
            {
                if (fastFirst)
                    yield return TimeSpan.Zero;

                for (int i = fastFirst ? 1 : 0; i < retryCount; i++)
                    yield return delay;
            }

            public static IEnumerable<TimeSpan> GenLinear(
                TimeSpan initialDelay, int retryCount, double factor, bool fastFirst)
            {
                if (fastFirst)
                    yield return TimeSpan.Zero;

                double ms = initialDelay.TotalMilliseconds;
                double increment = factor * ms;

                for (int i = fastFirst ? 1 : 0; i < retryCount; i++, ms += increment)
                    yield return TimeSpan.FromMilliseconds(ms);
            }

            public static IEnumerable<TimeSpan> GenExponential(
                TimeSpan initialDelay, int retryCount, double factor, bool fastFirst)
            {
                if (fastFirst)
                    yield return TimeSpan.Zero;

                double ms = initialDelay.TotalMilliseconds;

                for (int i = fastFirst ? 1 : 0; i < retryCount; i++, ms *= factor)
                    yield return TimeSpan.FromMilliseconds(ms);
            }

            /// <summary>
            /// Implements Microsoft's "decorrelated jitter" algorithm from AWS Architecture Blog.
            /// See: https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/
            /// </summary>
            public static IEnumerable<TimeSpan> GenJitter(TimeSpan medianFirstRetryDelay, int retryCount, bool fastFirst)
            {
                if (fastFirst)
                    yield return TimeSpan.Zero;

                long targetTicks = medianFirstRetryDelay.Ticks;
                double prev = 0.0;

                for (int i = fastFirst ? 1 : 0; i < retryCount; i++)
                {
                    // Intrinsic-value method: t = i + U(0,1), then transform.
                    double t = i + Random.Shared.NextDouble();
                    double next = Math.Pow(2, t) * Math.Tanh(Math.Sqrt(JitterConstants.PFactor * t));
                    double formulaIntrinsicValue = next - prev;

                    yield return TimeSpan.FromTicks(
                        (long)Math.Min(
                            formulaIntrinsicValue * JitterConstants.RpScalingFactor * targetTicks,
                            JitterConstants.MaxTicks));

                    prev = next;
                }
            }

            /// <summary>
            /// Constants for the decorrelated jitter algorithm (AWS blog reference).
            /// PFactor = 4.0 controls the shape of the distribution.
            /// RpScalingFactor ≈ 0.714 derives from the integral normalization.
            /// </summary>
            static class JitterConstants
            {
                public const double PFactor = 4.0;
                public const double RpScalingFactor = 1.0 / 1.4;
                public static readonly long MaxTicks = TimeSpan.MaxValue.Ticks - 1000;
            }
        }

        #endregion
    }

    #endregion
}
