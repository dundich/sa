namespace Sa.Outbox.Delivery;

/// <summary>
/// Exponential empty-queue wait strategy with jitter.
/// <para>
/// After <c>n</c> consecutive empty polls the wait is
/// <c>min(maxInterval, baseInterval * multiplier^(n-1))</c>, multiplied by a random factor in
/// <c>[1 - jitter, 1]</c> and clamped to <c>[baseInterval, maxInterval]</c>. The first empty poll
/// therefore keeps <paramref name="multiplier"/> out of the ramp and still waits the base
/// interval.
/// </para>
/// <para>
/// When <c>maxInterval</c> is <see cref="TimeSpan.Zero"/>, or not greater than <c>baseInterval</c>,
/// the base interval is returned unchanged — adaptive back-off is considered disabled.
/// </para>
/// </summary>
public sealed class ExponentialIdleWaitStrategy : IEmptyQueueWaitStrategy
{
    /// <summary>Shared instance with sensible defaults (x2 per empty poll, 20% jitter).</summary>
    public static readonly ExponentialIdleWaitStrategy Shared = new();

    private readonly double _multiplier;
    private readonly double _jitter;

    /// <summary>
    /// Creates a new exponential idle wait strategy.
    /// </summary>
    /// <param name="multiplier">Growth factor applied per consecutive empty poll. Must be &gt; 1. Defaults to 2.</param>
    /// <param name="jitter">Downward jitter fraction (0..1). Defaults to 0.2.</param>
    public ExponentialIdleWaitStrategy(double multiplier = 2.0, double jitter = 0.2)
    {
        _multiplier = multiplier > 1.0 ? multiplier : 2.0;
        _jitter = Math.Clamp(jitter, 0.0, 1.0);
    }

    /// <inheritdoc/>
    public TimeSpan GetNextWait(TimeSpan baseInterval, TimeSpan maxInterval, int consecutiveEmptyPolls)
    {
        if (baseInterval <= TimeSpan.Zero) return baseInterval;

        // A Zero (/sub-base) cap means "adaptive back-off disabled" — stay fixed.
        if (maxInterval <= baseInterval) return baseInterval;

        int n = Math.Max(1, consecutiveEmptyPolls);

        double factor = Math.Pow(_multiplier, n - 1);
        double ticks = baseInterval.Ticks * factor;

        double jitterFactor = 1.0 - _jitter + Random.Shared.NextDouble() * _jitter;
        ticks *= jitterFactor;

        if (ticks < baseInterval.Ticks) ticks = baseInterval.Ticks;
        else if (ticks > maxInterval.Ticks) ticks = maxInterval.Ticks;

        return TimeSpan.FromTicks((long)ticks);
    }
}
