namespace Sa.Outbox.Redis.Configuration;

/// <summary>Where a consumer group starts reading when it is created on an existing stream.</summary>
public enum RedisOutboxGroupStart
{
    /// <summary>
    /// Start at the first message still inside the group's own lookback window — the delivery
    /// setting <c>LookbackInterval</c> as carried by <c>OutboxMessageFilter.FromDate</c>. This is
    /// the recommended mode: a new consumer group drains recent history instead of starting empty.
    /// </summary>
    Lookback = 0,

    /// <summary>Start from the very beginning of the stream.</summary>
    Beginning = 1,

    /// <summary>Start from the current end: a new group sees only messages published after it is created.</summary>
    Now = 2,
}

/// <summary>Consumption-side settings of the Redis Outbox.</summary>
public sealed class RedisOutboxConsumeSettings
{
    /// <summary>Contains default values for the consumption-side settings.</summary>
    public static class Defaults
    {
        /// <summary>
        /// The default minimum idle time of a pending entry before another worker may claim it
        /// (10 minutes).
        /// </summary>
        public static TimeSpan PendingClaimIdle => TimeSpan.FromMinutes(10);

        /// <summary>The default number of deliveries after which a pending entry is dead-lettered (5).</summary>
        public const int MaxDeliveryAttempts = 5;
    }

    /// <summary>
    /// Gets or sets where a newly created Redis consumer group starts. Default is
    /// <see cref="RedisOutboxGroupStart.Lookback"/>.
    /// </summary>
    /// <remarks>
    /// The start position only matters once, when the group is created on the stream: it is what
    /// the group then remembers as its cursor. Changing it later does not move an existing group.
    /// </remarks>
    public RedisOutboxGroupStart StartFrom { get; set; } = RedisOutboxGroupStart.Lookback;

    /// <summary>
    /// Gets or sets how long an entry may stay pending (unacknowledged) before another worker may
    /// claim it. Default is 10 minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only retry clock of the provider: a message whose delivery did not acknowledge it
    /// — a consumer that failed, a worker that crashed, a postponed/retried message — is picked up by
    /// <c>XAUTOCLAIM</c> once it has been idle for at least this long.
    /// </para>
    /// <para>
    /// Keep it comfortably above the consumer's <c>LockDuration</c>: the provider renews the idle
    /// time of the entries it is currently processing (<c>ExtendDelivery</c>), but a value shorter
    /// than the processing time would let another worker claim a live batch.
    /// </para>
    /// </remarks>
    public TimeSpan PendingClaimIdle { get; set; } = Defaults.PendingClaimIdle;

    /// <summary>
    /// Gets or sets the number of deliveries after which a pending entry is acknowledged and handed
    /// to <see cref="Sa.Outbox.Redis.Poison.IRedisOutboxPoisonHandler"/> instead of being claimed again. Default is 5.
    /// </summary>
    /// <remarks>
    /// This protects against poison messages: a message that crashes the consumer never reaches
    /// <c>ReturnDelivery</c>, so the consumer-level <c>MaxDeliveryAttempts</c> never sees it. The
    /// delivery counter maintained by Redis in the pending entries list does, and after this many
    /// deliveries the entry is passed to the poison handler and acknowledged.
    /// <para>
    /// Every claim counts, including a reclaim of a message the consumer postponed with
    /// <c>Postpone</c>. The stream-only model has no separate retry counter that could exempt it.
    /// </para>
    /// </remarks>
    public int MaxDeliveryAttempts { get; set; } = Defaults.MaxDeliveryAttempts;

    internal void Validate()
    {
        if (!Enum.IsDefined(StartFrom))
            throw new ArgumentOutOfRangeException(nameof(StartFrom), StartFrom, "Unknown Redis Outbox group start mode.");

        if (PendingClaimIdle <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PendingClaimIdle), PendingClaimIdle, "PendingClaimIdle must be > TimeSpan.Zero.");

        if (MaxDeliveryAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDeliveryAttempts), MaxDeliveryAttempts, "MaxDeliveryAttempts must be > 0.");
    }
}
