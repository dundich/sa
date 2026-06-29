namespace Sa.Outbox.Delivery;

/// <summary>
/// Immutable settings snapshot for a single outbox consumer group.
/// This is the single source of truth — all delivery code reads from this type only.
/// Create new instances via <see cref="OutboxConsumerSettingsBuilder"/> or the <c>with</c> expression.
/// </summary>
public sealed record OutboxConsumerSettings(

    Guid Id,

    /// <summary>
    /// Unique identifier for the consumer group. Groups settings for a single logical consumer.
    /// </summary>
    string ConsumerGroupId,

    /// <summary>
    /// When true — the consumer runs as a singleton (one instance across the entire cluster).
    /// When false — each application instance runs its own consumer copy.
    /// </summary>
    bool AsSingleton,

    /// <summary>
    /// Periodicity of the consumer run (interval between processing iterations).
    /// </summary>
    TimeSpan Interval,

    /// <summary>
    /// Initial delay before the consumer's first run after application startup.
    /// </summary>
    TimeSpan InitialDelay,

    /// <summary>
    /// Maximum number of concurrent threads for message processing at the consumer group level.
    /// </summary>
    int ConcurrencyLimit,

    /// <summary>
    /// Maximum number of simultaneously running processors (jobs) within this group.
    /// </summary>
    int MaxConcurrency,

    /// <summary>
    /// Number of retry attempts on message processing error.
    /// -1 means infinite retries.
    /// </summary>
    int RetryCountOnError,

    /// <summary>
    /// Maximum batch size (number of messages) per processing iteration.
    /// </summary>
    int MaxBatchSize,

    /// <summary>
    /// Maximum number of processing iterations per cycle (-1 = unlimited).
    /// </summary>
    int MaxProcessingIterations,

    /// <summary>
    /// Delay between processing iterations within a single cycle.
    /// </summary>
    TimeSpan IterationDelay,

    /// <summary>
    /// Lock duration for record processing (lock TTL).
    /// The record is locked from other consumers for this period.
    /// </summary>
    TimeSpan LockDuration,

    /// <summary>
    /// Lock renewal interval.
    /// Must be less than LockDuration.
    /// </summary>
    TimeSpan LockRenewal,

    /// <summary>
    /// Lookback interval — how far back in time to search for unprocessed messages.
    /// Used to catch up messages that may have been missed during idle periods.
    /// </summary>
    TimeSpan LookbackInterval,

    /// <summary>
    /// Maximum delivery attempt count for a message before sending it to the dead-letter queue (DLQ).
    /// </summary>
    int MaxDeliveryAttempts,

    /// <summary>
    /// Time window for aggregating messages into a batch. Messages accumulated within this window are processed together.
    /// </summary>
    TimeSpan BatchingWindow,

    /// <summary>
    /// Timeout for processing a single tenant's data.
    /// Exceeding this timeout aborts the tenant's processing.
    /// </summary>
    TimeSpan PerTenantTimeout,

    /// <summary>
    /// Maximum degree of parallelism for tenant-level processing.
    /// 1 = sequential, >1 = parallel.
    /// </summary>
    int PerTenantMaxDegreeOfParallelism,

    /// <summary>
    /// Consumer pause flag. When true, processing is suspended but the consumer remains active.
    /// Allows temporarily halting processing without stopping the entire service.
    /// </summary>
    bool Paused,

    /// <summary>
    /// Settings version. Incremented on every change for change detection.
    /// Used for optimistic locking and notifying subscribers.
    /// </summary>
    int Version)
{
    /// <summary>
    /// Validates all settings and returns a list of error messages.
    /// Empty list means valid.
    /// </summary>
#pragma warning disable S3776
    public List<string> Validate()
#pragma warning restore S3776
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ConsumerGroupId))
            errors.Add("ConsumerGroupId cannot be null or empty.");

        if (Interval.Ticks < 0)
            errors.Add($"Interval must be >= TimeSpan.Zero, got {Interval}.");

        if (InitialDelay.Ticks < 0)
            errors.Add($"InitialDelay must be >= TimeSpan.Zero, got {InitialDelay}.");

        if (ConcurrencyLimit <= 0)
            errors.Add($"ConcurrencyLimit must be > 0, got {ConcurrencyLimit}.");

        if (MaxConcurrency <= 0)
            errors.Add($"MaxConcurrency must be > 0, got {MaxConcurrency}.");

        if (RetryCountOnError < 0)
            errors.Add($"RetryCountOnError must be >= 0, got {RetryCountOnError}.");

        if (MaxBatchSize <= 0)
            errors.Add($"MaxBatchSize must be > 0, got {MaxBatchSize}.");

        if (MaxProcessingIterations < -1)
            errors.Add($"MaxProcessingIterations must be >= -1, got {MaxProcessingIterations}.");

        if (IterationDelay.Ticks < 0)
            errors.Add($"IterationDelay must be >= TimeSpan.Zero, got {IterationDelay}.");

        if (LockDuration <= TimeSpan.Zero)
            errors.Add($"LockDuration must be > TimeSpan.Zero, got {LockDuration}.");

        if (LockRenewal >= LockDuration)
            errors.Add($"LockRenewal ({LockRenewal}) must be less than LockDuration ({LockDuration}).");

        if (LockRenewal.Ticks < 0)
            errors.Add($"LockRenewal must be >= TimeSpan.Zero, got {LockRenewal}.");

        if (LookbackInterval.Ticks <= 0)
            errors.Add($"LookbackInterval must be > TimeSpan.Zero, got {LookbackInterval}.");

        if (MaxDeliveryAttempts <= 0)
            errors.Add($"MaxDeliveryAttempts must be > 0, got {MaxDeliveryAttempts}.");

        if (BatchingWindow.Ticks < 0)
            errors.Add($"BatchingWindow must be >= TimeSpan.Zero, got {BatchingWindow}.");

        if (PerTenantTimeout.Ticks < 0)
            errors.Add($"PerTenantTimeout must be >= TimeSpan.Zero, got {PerTenantTimeout}.");

        if (PerTenantMaxDegreeOfParallelism == 0)
            errors.Add(
                "PerTenantMaxDegreeOfParallelism cannot be 0. Use 1 for sequential or > 1 for parallel.");

        return errors;
    }

    /// <summary>
    /// Validates all settings, throwing <see cref="InvalidOperationException"/> if invalid.
    /// </summary>
    public void ThrowIfInvalid()
    {
        var errors = Validate();
        if (errors.Count != 0)
            throw new InvalidOperationException(
                $"Invalid OutboxConsumerSettings: {string.Join("; ", errors)}");
    }

    // ── Equality helper ───────────────────────────────────────

    /// <inheritdoc/>
    public override string ToString()
        => $"{ConsumerGroupId} interval={Interval}, batchSize={MaxBatchSize}, " +
           $"parallelism={PerTenantMaxDegreeOfParallelism}, paused={Paused}";
}
