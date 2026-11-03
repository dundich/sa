namespace Sa.Outbox.Delivery;

/// <summary>
/// Fixed empty-queue wait strategy: the poll period always stays at
/// <see cref="OutboxConsumerSettings.Interval"/> — exactly the pre-existing behavior.
/// <para>
/// This is the explicit opt-out. Register it with
/// <c>WithIdleWaitStrategy&lt;FixedIntervalWaitStrategy&gt;()</c> to pin the algorithm, or use
/// <c>WithFixedInterval()</c> to drop the per-group back-off cap. The DI default is
/// <see cref="ExponentialIdleWaitStrategy"/>, which degrades to this behavior while
/// <see cref="OutboxConsumerSettings.IdleMaxInterval"/> is <see cref="TimeSpan.Zero"/>.
/// </para>
/// </summary>
/// <remarks>
/// Adaptive back-off is opt-in per group via <c>WithIdleBackoff(maxInterval)</c>, so the two
/// strategies are behaviorally equivalent in the default configuration.
/// </remarks>
public sealed class FixedIntervalWaitStrategy : IEmptyQueueWaitStrategy
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly FixedIntervalWaitStrategy Shared = new();

    /// <inheritdoc/>
    public TimeSpan GetNextWait(TimeSpan baseInterval, TimeSpan maxInterval, int consecutiveEmptyPolls)
        => baseInterval;
}
