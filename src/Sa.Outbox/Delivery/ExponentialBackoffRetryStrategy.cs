using System.Runtime.CompilerServices;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Default exponential backoff with jitter retry strategy.
/// Formula: min(maxDelay, baseDelay * 2^(attempt-1)) * jitterFactor
/// </summary>
public sealed class ExponentialBackoffRetryStrategy : IRetryStrategy
{
    /// <summary>
    /// The shared singleton instance using sensible defaults (5s base, 30min max).
    /// </summary>
    public static readonly ExponentialBackoffRetryStrategy Shared = new();

    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterFactorMin;
    private readonly double _jitterFactorMax;

    /// <summary>
    /// Creates a new exponential backoff strategy.
    /// </summary>
    /// <param name="baseDelay">Base delay for the first retry. Defaults to 5 seconds.</param>
    /// <param name="maxDelay">Maximum cap on backoff. Defaults to 30 minutes.</param>
    /// <param name="jitterFactorMin">Minimum jitter multiplier (0..1). Defaults to 0.5.</param>
    /// <param name="jitterFactorMax">Maximum jitter multiplier (jitterFactorMin..1). Defaults to 1.0.</param>
    public ExponentialBackoffRetryStrategy(
        TimeSpan baseDelay = default,
        TimeSpan maxDelay = default,
        double jitterFactorMin = 0.5,
        double jitterFactorMax = 1.0)
    {
        _baseDelay = baseDelay == TimeSpan.Zero ? TimeSpan.FromSeconds(5) : baseDelay;
        _maxDelay = maxDelay == TimeSpan.Zero ? TimeSpan.FromMinutes(30) : maxDelay;
        _jitterFactorMin = Math.Clamp(jitterFactorMin, 0.0, 1.0);
        _jitterFactorMax = Math.Clamp(Math.Max(jitterFactorMax, jitterFactorMin), _jitterFactorMin, 1.0);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeSpan GetBackoff(int attemptNumber)
    {
        if (attemptNumber < 1) attemptNumber = 1;

        var exponent = Math.Pow(2.0, attemptNumber - 1);
        var cappedDelay = TimeSpan.FromTicks((long)(_baseDelay.Ticks * Math.Min(exponent, _maxDelay.Ticks / (double)_baseDelay.Ticks)));

        var jitter = _jitterFactorMin + Random.Shared.NextDouble() * (_jitterFactorMax - _jitterFactorMin);
        var totalTicks = (long)(cappedDelay.Ticks * jitter);

        return TimeSpan.FromTicks(totalTicks);
    }
}
