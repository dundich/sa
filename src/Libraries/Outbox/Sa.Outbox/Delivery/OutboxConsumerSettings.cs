namespace Sa.Outbox.Delivery;

/// <summary>
/// Immutable settings snapshot for a single outbox consumer group.
/// This is the single source of truth — all delivery code reads from this type only.
/// Create new instances via <see cref="OutboxConsumerSettingsBuilder"/> or the <c>with</c> expression.
/// </summary>
public sealed record OutboxConsumerSettings
{
    /// <summary>Unique identifier for the consumer group. Groups settings for a single logical consumer.</summary>
    public required string ConsumerGroupId { get; init; }

    /// <summary>When true — the consumer runs as a singleton (one instance across the entire cluster). When false — each application instance runs its own consumer copy.</summary>
    public required bool AsSingleton { get; init; }

    /// <summary>Periodicity of the consumer run (interval between processing iterations).</summary>
    public required TimeSpan Interval { get; init; }

    /// <summary>Initial delay before the consumer's first run after application startup.</summary>
    public required TimeSpan InitialDelay { get; init; }

    /// <summary>Maximum number of concurrent threads for message processing at the consumer group level.</summary>
    public required int ConcurrencyLimit { get; init; }

    /// <summary>Maximum number of simultaneously running processors (jobs) within this group.</summary>
    public required int MaxConcurrency { get; init; }

    /// <summary>Number of job retry attempts on error. Must be non-negative; <see cref="OutboxConsumerSettingsBuilder.WithInfiniteRetries"/> sets <see cref="int.MaxValue"/>, which is the practical "no limit".</summary>
    public required int RetryCountOnError { get; init; }

    /// <summary>Maximum batch size (number of messages) per processing iteration.</summary>
    public required int MaxBatchSize { get; init; }

    /// <summary>Maximum number of processing iterations per cycle (-1 = unlimited).</summary>
    public required int MaxProcessingIterations { get; init; }

    /// <summary>Delay between processing iterations within a single cycle.</summary>
    public required TimeSpan IterationDelay { get; init; }

    /// <summary>Lock duration for record processing (lock TTL). Must be greater than TimeSpan.Zero — a task without a TTL would never become re-rentable after a worker crash. The record is locked from other consumers for this period.</summary>
    public required TimeSpan LockDuration { get; init; }

    /// <summary>Lock renewal interval. Must be greater than TimeSpan.Zero (zero would break the renewal timer) and less than LockDuration.</summary>
    public required TimeSpan LockRenewal { get; init; }

    /// <summary>Lookback interval — how far back in time to search for unprocessed messages. Used to catch up messages that may have been missed during idle periods.</summary>
    public required TimeSpan LookbackInterval { get; init; }

    /// <summary>Maximum delivery attempt count for a message before sending it to the dead-letter queue (DLQ).</summary>
    public required int MaxDeliveryAttempts { get; init; }

    /// <summary>Time window for aggregating messages into a batch. Messages accumulated within this window are processed together.</summary>
    public required TimeSpan BatchingWindow { get; init; }

    /// <summary>Timeout for processing a single tenant's data. Exceeding this timeout aborts the tenant's processing.</summary>
    public required TimeSpan PerTenantTimeout { get; init; }

    /// <summary>Maximum degree of parallelism for tenant-level processing. 1 = sequential, >1 = parallel.</summary>
    public required int PerTenantMaxDegreeOfParallelism { get; init; }

    /// <summary>Consumer pause flag. When true, processing is suspended but the consumer remains active. Allows temporarily halting processing without stopping the entire service.</summary>
    public required bool Paused { get; init; }

    /// <summary>Settings version. Incremented on every change for change detection. Used for optimistic locking and notifying subscribers.</summary>
    public required int Version { get; init; }

    /// <summary>The provider key of the storage this group belongs to (equal to the outbox scope key). The delivery pipeline resolves the keyed storage contracts by this value; a single-provider application keeps <see cref="OutboxScope.DefaultKey"/>.</summary>
    public string ProviderKey { get; init; } = OutboxScope.DefaultKey;

    /// <summary>Upper bound for the adaptive poll interval used while the consumer group's queue stays empty. <see cref="TimeSpan.Zero"/> (default) disables adaptive back-off — the poll period stays at <see cref="Interval"/>. When set, it must be at least <see cref="Interval"/>, and the wait between empty polls grows from <see cref="Interval"/> towards this bound according to <see cref="IEmptyQueueWaitStrategy"/>.</summary>
    public TimeSpan IdleMaxInterval { get; init; }

    /// <summary>Free-form application tag carried with the settings; not used by the pipeline.</summary>
    public object? Tag { get; init; }
    /// <summary>
    /// Validates all settings and returns a list of error messages.
    /// Empty list means valid.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ConsumerGroupId))
            errors.Add("ConsumerGroupId cannot be null or empty.");

        // TimeSpans: non-negative (>= Zero)
        CheckTsNonNeg(nameof(Interval), Interval);
        CheckTsNonNeg(nameof(InitialDelay), InitialDelay);
        CheckTsNonNeg(nameof(IterationDelay), IterationDelay);
        CheckTsNonNeg(nameof(LockRenewal), LockRenewal);
        CheckTsNonNeg(nameof(BatchingWindow), BatchingWindow);
        CheckTsNonNeg(nameof(PerTenantTimeout), PerTenantTimeout);
        CheckTsNonNeg(nameof(IdleMaxInterval), IdleMaxInterval);

        // TimeSpans: strictly positive (> Zero)
        CheckTsPos(nameof(LockDuration), LockDuration);
        CheckTsPos(nameof(LookbackInterval), LookbackInterval);

        // Integers: non-negative (>= 0)
        CheckIntNonNeg(nameof(ConcurrencyLimit), ConcurrencyLimit);
        CheckIntNonNeg(nameof(RetryCountOnError), RetryCountOnError);

        // Integers: strictly positive (> 0)
        CheckIntPos(nameof(MaxBatchSize), MaxBatchSize);
        CheckIntPos(nameof(MaxConcurrency), MaxConcurrency);
        CheckIntPos(nameof(MaxDeliveryAttempts), MaxDeliveryAttempts);

        // Special: >= -1
        CheckIntGeMinus1(nameof(MaxProcessingIterations), MaxProcessingIterations);

        // Cross-field validations
        // Valid values are -1 (all processors) or a positive degree; both 0 and values below -1
        // would throw from ParallelOptions.MaxDegreeOfParallelism during delivery.
        if (PerTenantMaxDegreeOfParallelism == 0 || PerTenantMaxDegreeOfParallelism < -1)
        {
            errors.Add(
                $"PerTenantMaxDegreeOfParallelism cannot be {PerTenantMaxDegreeOfParallelism}. " +
                "Use -1 for all processors, 1 for sequential, or > 1 for parallel.");
        }

        if (string.IsNullOrWhiteSpace(ProviderKey))
            errors.Add("ProviderKey cannot be null or empty.");

        if (LockRenewal <= TimeSpan.Zero || LockRenewal >= LockDuration)
        {
            errors.Add(
                $"LockRenewal ({LockRenewal}) must be greater than TimeSpan.Zero and less than LockDuration ({LockDuration}).");
        }

        if (IdleMaxInterval > TimeSpan.Zero && IdleMaxInterval < Interval)
        {
            errors.Add(
                $"IdleMaxInterval ({IdleMaxInterval}) must be greater than or equal to Interval ({Interval}) when set.");
        }

        void CheckTsNonNeg(string name, TimeSpan ts)
        {
            if (ts.Ticks < 0)
                errors.Add($"{name} must be >= TimeSpan.Zero, got {ts}.");
        }

        void CheckTsPos(string name, TimeSpan ts)
        {
            if (ts <= TimeSpan.Zero)
                errors.Add($"{name} must be > TimeSpan.Zero, got {ts}.");
        }

        void CheckIntNonNeg(string name, int value)
        {
            if (value < 0)
                errors.Add($"{name} must be >= 0, got {value}.");
        }

        void CheckIntPos(string name, int value)
        {
            if (value <= 0)
                errors.Add($"{name} must be > 0, got {value}.");
        }

        void CheckIntGeMinus1(string name, int value)
        {
            if (value < -1)
                errors.Add($"{name} must be >= -1, got {value}.");
        }

        return errors;
    }

    /// <summary>
    /// Validates all settings, throwing <see cref="InvalidOperationException"/> if invalid.
    /// </summary>
    public void ThrowIfInvalid()
    {
        var errors = Validate();
        if (errors.Count != 0)
        {
            throw new InvalidOperationException(
                $"Invalid OutboxConsumerSettings: {string.Join("; ", errors)}");
        }
    }

    // ── Equality helper ───────────────────────────────────────

    /// <inheritdoc/>
    public override string ToString()
        => $"{ProviderKey}/{ConsumerGroupId} v{Version} interval={Interval}, batchSize={MaxBatchSize}, " +
           $"parallelism={PerTenantMaxDegreeOfParallelism}, paused={Paused}";
}
