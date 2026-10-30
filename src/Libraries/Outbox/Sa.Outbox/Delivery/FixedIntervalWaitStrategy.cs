namespace Sa.Outbox.Delivery;

/// <summary>
/// Default empty-queue wait strategy: the poll period always stays at
/// <see cref="OutboxConsumerSettings.Interval"/> — exactly the pre-existing behavior.
/// </summary>
/// <remarks>
/// This is also what the adaptive strategy degrades to when
/// <see cref="OutboxConsumerSettings.IdleMaxInterval"/> is left at <see cref="TimeSpan.Zero"/>,
/// so the two are behaviorally equivalent in the default configuration.
/// </remarks>
public sealed class FixedIntervalWaitStrategy : IEmptyQueueWaitStrategy
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly FixedIntervalWaitStrategy Shared = new();

    /// <inheritdoc/>
    public TimeSpan GetNextWait(TimeSpan baseInterval, TimeSpan maxInterval, int consecutiveEmptyPolls)
        => baseInterval;
}
