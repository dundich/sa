namespace Sa.Outbox.Delivery;

/// <summary>
/// Immutable default values used by <see cref="OutboxConsumerSettings"/> and <see cref="OutboxConsumerSettingsBuilder"/>.
/// Change here to affect all consumers globally.
/// </summary>
public static class OutboxDefaults
{
    /// <summary>Default interval between job executions (1 minute).</summary>
    public static TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>Default initial delay before the first execution (10 seconds).</summary>
    public static TimeSpan InitialDelay => TimeSpan.FromSeconds(10);

    /// <summary>Default concurrency limit (1).</summary>
    public static int ConcurrencyLimit => 1;

    /// <summary>Default singleton mode True.</summary>
    public static bool AsSingleton => true;

    /// <summary>Default maximum concurrency (48).</summary>
    /// <remarks>
    /// High value designed for cloud deployments with connection pooling and
    /// partitioned outbox tables. For local/small deployments override via builder.
    /// </remarks>
    public static int MaxConcurrency => 48;

    /// <summary>Default retry count on error — no retries.</summary>
    public static int RetryCountOnError => 0;

    /// <summary>Default maximum batch size for database polling (128).</summary>
    /// <remarks>
    /// The cost of one poll is dominated by its fixed part — the transaction, the advisory lock, the
    /// cursor read and the write round trip — not by the rows it copies: measured on a 7 × 40k outbox
    /// the same poll took 11.5 ms at batch 16 and 13.8 ms at batch 512, i.e. ~0.027 ms per extra row.
    /// At 16 that fixed cost buys ~1 400 rows/s per group; at 128 it buys ~10 200, at 512 ~38 000.
    ///
    /// Keep the coupling with <see cref="LockDuration"/> / <see cref="LockRenewal"/> in mind when
    /// raising it further: a batch must be processable inside LockDuration, and the renewal statement
    /// updates every rented row of the batch. 128 also keeps SqlFinishDelivery at two statements
    /// (1024 parameters / 11 per row = 93 rows per statement) instead of three.
    /// </remarks>
    public static int MaxBatchSize => 128;

    /// <summary>Default maximum processing iterations (10).</summary>
    public static int MaxProcessingIterations => 10;

    /// <summary>Default iteration delay (zero — greedy mode).</summary>
    public static TimeSpan IterationDelay => TimeSpan.Zero;

    /// <summary>Default message lock duration (10 seconds).</summary>
    public static TimeSpan LockDuration => TimeSpan.FromSeconds(10);

    /// <summary>Default lock renewal time (3 seconds).</summary>
    public static TimeSpan LockRenewal => TimeSpan.FromSeconds(3);

    /// <summary>Default lookback interval for selecting messages (7 days).</summary>
    public static TimeSpan LookbackInterval => TimeSpan.FromDays(7);

    /// <summary>Default maximum delivery attempts (3).</summary>
    public static int MaxDeliveryAttempts => 3;

    /// <summary>Default batching window for accumulating messages (3 seconds).</summary>
    public static TimeSpan BatchingWindow => TimeSpan.FromSeconds(3);

    /// <summary>Default per-tenant processing timeout (zero — no timeout).</summary>
    public static TimeSpan PerTenantTimeout => TimeSpan.Zero;

    /// <summary>Default max degree of tenant parallelism (sequential = 1).</summary>
    public static int PerTenantMaxDegreeOfParallelism => 1;

    /// <summary>Default paused state (false — running).</summary>
    public static bool Paused => false;

    /// <summary>
    /// Default upper bound for the adaptive empty-queue poll interval (zero — adaptive back-off
    /// disabled, the poll period stays at <see cref="Interval"/>).
    /// </summary>
    public static TimeSpan IdleMaxInterval => TimeSpan.Zero;
}
