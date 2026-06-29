namespace Sa.Outbox.Delivery;

/// <summary>
/// Immutable settings snapshot for a single outbox consumer group.
/// This is the single source of truth — all delivery code reads from this type only.
/// Create new instances via <see cref="OutboxConsumerSettingsBuilder"/> or the <c>with</c> expression.
/// </summary>
public sealed record OutboxConsumerSettings(
    string ConsumerGroupId,
    bool AsSingleton,
    TimeSpan Interval,
    TimeSpan InitialDelay,
    int ConcurrencyLimit,
    int MaxConcurrency,
    int RetryCountOnError,
    int MaxBatchSize,
    int MaxProcessingIterations,
    TimeSpan IterationDelay,
    TimeSpan LockDuration,
    TimeSpan LockRenewal,
    TimeSpan LookbackInterval,
    int MaxDeliveryAttempts,
    TimeSpan BatchingWindow,
    TimeSpan PerTenantTimeout,
    int PerTenantMaxDegreeOfParallelism,
    bool Paused
    //int Version
    )
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

    // ── Fluent with-helpers ───────────────────────────────────
    // Each returns a NEW instance with one field changed and version incremented.

    /// <summary>
    /// Creates a copy with a new <see cref="Interval"/>.
    /// </summary>
    public OutboxConsumerSettings WithInterval(TimeSpan interval)
        => this with { Interval = interval };

    /// <summary>
    /// Creates a copy with a new <see cref="InitialDelay"/>.
    /// </summary>
    public OutboxConsumerSettings WithInitialDelay(TimeSpan initialDelay)
        => this with { InitialDelay = initialDelay };

    /// <summary>
    /// Creates a copy with a new <see cref="ConcurrencyLimit"/>.
    /// </summary>
    public OutboxConsumerSettings WithConcurrencyLimit(int concurrencyLimit)
        => this with { ConcurrencyLimit = concurrencyLimit };

    /// <summary>
    /// Creates a copy with a new <see cref="MaxConcurrency"/>.
    /// </summary>
    public OutboxConsumerSettings WithMaxConcurrency(int maxConcurrency)
        => this with { MaxConcurrency = maxConcurrency };

    /// <summary>
    /// Creates a copy with a new <see cref="RetryCountOnError"/>.
    /// </summary>
    public OutboxConsumerSettings WithRetryCountOnError(int retryCountOnError)
        => this with { RetryCountOnError = retryCountOnError };

    /// <summary>
    /// Creates a copy with a new <see cref="MaxBatchSize"/>.
    /// </summary>
    public OutboxConsumerSettings WithMaxBatchSize(int maxBatchSize)
        => this with { MaxBatchSize = maxBatchSize };

    /// <summary>
    /// Creates a copy with a new <see cref="MaxProcessingIterations"/>.
    /// </summary>
    public OutboxConsumerSettings WithMaxProcessingIterations(int maxProcessingIterations)
        => this with { MaxProcessingIterations = Math.Max(-1, maxProcessingIterations) };

    /// <summary>
    /// Creates a copy with a new <see cref="IterationDelay"/>.
    /// </summary>
    public OutboxConsumerSettings WithIterationDelay(TimeSpan iterationDelay)
        => this with { IterationDelay = iterationDelay };

    /// <summary>
    /// Creates a copy with a new <see cref="LockDuration"/>.
    /// </summary>
    public OutboxConsumerSettings WithLockDuration(TimeSpan lockDuration)
        => this with { LockDuration = lockDuration };

    /// <summary>
    /// Creates a copy with a new <see cref="LockRenewal"/>.
    /// </summary>
    public OutboxConsumerSettings WithLockRenewal(TimeSpan lockRenewal)
        => this with { LockRenewal = lockRenewal };

    /// <summary>
    /// Creates a copy with a new <see cref="LookbackInterval"/>.
    /// </summary>
    public OutboxConsumerSettings WithLookbackInterval(TimeSpan lookbackInterval)
        => this with { LookbackInterval = lookbackInterval };

    /// <summary>
    /// Creates a copy with a new <see cref="MaxDeliveryAttempts"/>.
    /// </summary>
    public OutboxConsumerSettings WithMaxDeliveryAttempts(int maxDeliveryAttempts)
        => this with { MaxDeliveryAttempts = maxDeliveryAttempts };

    /// <summary>
    /// Creates a copy with a new <see cref="BatchingWindow"/>.
    /// </summary>
    public OutboxConsumerSettings WithBatchingWindow(TimeSpan batchingWindow)
        => this with { BatchingWindow = batchingWindow };

    /// <summary>
    /// Creates a copy with a new <see cref="PerTenantTimeout"/>.
    /// </summary>
    public OutboxConsumerSettings WithPerTenantTimeout(TimeSpan perTenantTimeout)
        => this with { PerTenantTimeout = perTenantTimeout };

    /// <summary>
    /// Creates a copy with a new <see cref="PerTenantMaxDegreeOfParallelism"/>.
    /// </summary>
    public OutboxConsumerSettings WithPerTenantMaxDegreeOfParallelism(int perTenantMaxDegreeOfParallelism)
        => this with { PerTenantMaxDegreeOfParallelism = perTenantMaxDegreeOfParallelism };

    /// <summary>
    /// Creates a paused copy of these settings.
    /// </summary>
    public OutboxConsumerSettings WithPaused(bool paused)
        => this with { Paused = paused };

    // ── Equality helper ───────────────────────────────────────

    /// <inheritdoc/>
    public override string ToString()
        => $"{ConsumerGroupId} interval={Interval}, batchSize={MaxBatchSize}, " +
           $"parallelism={PerTenantMaxDegreeOfParallelism}, paused={Paused}";
}
