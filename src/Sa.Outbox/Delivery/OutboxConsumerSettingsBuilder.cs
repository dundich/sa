namespace Sa.Outbox.Delivery;

/// <summary>
/// Fluent builder for creating or mutating <see cref="OutboxConsumerSettings"/>.
/// Use for bootstrap configuration (startup) and partial runtime updates via <see cref="BuildCopy(OutboxConsumerSettings)"/>.
/// </summary>
public sealed class OutboxConsumerSettingsBuilder
{
    private string? _consumerGroupId;
    private bool? _asSingleton;
    private TimeSpan? _interval;
    private TimeSpan? _initialDelay;
    private int? _concurrencyLimit;
    private int? _maxConcurrency;
    private int? _retryCountOnError;
    private int? _maxBatchSize;
    private int? _maxProcessingIterations;
    private TimeSpan? _iterationDelay;
    private TimeSpan? _lockDuration;
    private TimeSpan? _lockRenewal;
    private TimeSpan? _lookbackInterval;
    private int? _maxDeliveryAttempts;
    private TimeSpan? _batchingWindow;
    private TimeSpan? _perTenantTimeout;
    private int? _perTenantMaxDegreeOfParallelism;
    private bool? _paused;

    // ── Bootstrap: build from scratch ─────────────────────────

    /// <summary>
    /// Builds a new <see cref="OutboxConsumerSettings"/> from the configured values.
    /// All unspecified fields receive sensible defaults.
    /// </summary>
    public OutboxConsumerSettings Build()
    {
        return new OutboxConsumerSettings(
            _consumerGroupId ?? throw new InvalidOperationException("ConsumerGroupId is required."),
            _asSingleton ?? OutboxDefaults.AsSingleton,
            _interval ?? OutboxDefaults.Interval,
            _initialDelay ?? OutboxDefaults.InitialDelay,
            _concurrencyLimit ?? OutboxDefaults.ConcurrencyLimit,
            _maxConcurrency ?? OutboxDefaults.MaxConcurrency,
            _retryCountOnError ?? OutboxDefaults.RetryCountOnError,
            _maxBatchSize ?? OutboxDefaults.MaxBatchSize,
            _maxProcessingIterations ?? OutboxDefaults.MaxProcessingIterations,
            _iterationDelay ?? OutboxDefaults.IterationDelay,
            _lockDuration ?? OutboxDefaults.LockDuration,
            _lockRenewal ?? OutboxDefaults.LockRenewal,
            _lookbackInterval ?? OutboxDefaults.LookbackInterval,
            _maxDeliveryAttempts ?? OutboxDefaults.MaxDeliveryAttempts,
            _batchingWindow ?? OutboxDefaults.BatchingWindow,
            _perTenantTimeout ?? OutboxDefaults.PerTenantTimeout,
            _perTenantMaxDegreeOfParallelism ?? OutboxDefaults.PerTenantMaxDegreeOfParallelism,
            _paused ?? OutboxDefaults.Paused,
            0);
    }


    // ── Fluent setters ────────────────────────────────────────

    /// <summary>
    /// Sets the consumer group identifier. Required for <see cref="Build"/>.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithConsumerGroupId(string consumerGroupId)
    {
        _consumerGroupId = consumerGroupId ?? throw new ArgumentNullException(nameof(consumerGroupId));
        return this;
    }

    /// <summary>
    /// Sets singleton lifetime for the associated consumer.
    /// </summary>
    public OutboxConsumerSettingsBuilder AsSingleton(bool value = true)
    {
        _asSingleton = value;
        return this;
    }

    // Schedule

    /// <summary>
    /// Sets the interval between job executions.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithInterval(TimeSpan interval)
    {
        _interval = interval;
        return this;
    }

    /// <summary>
    /// Sets the initial delay before the first execution.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithInitialDelay(TimeSpan initialDelay)
    {
        _initialDelay = initialDelay;
        return this;
    }

    /// <summary>
    /// Starts immediately (zero initial delay).
    /// </summary>
    public OutboxConsumerSettingsBuilder StartImmediately()
    {
        _initialDelay = TimeSpan.Zero;
        return this;
    }

    /// <summary>
    /// Sets the starting concurrency limit.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithConcurrencyLimit(int concurrencyLimit)
    {
        if (concurrencyLimit <= 0) throw new ArgumentException("ConcurrencyLimit must be > 0.", nameof(concurrencyLimit));
        _concurrencyLimit = concurrencyLimit;
        return this;
    }

    /// <summary>
    /// Sets the maximum concurrency allowed.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithMaxConcurrency(int maxConcurrency)
    {
        if (maxConcurrency <= 0) throw new ArgumentException("MaxConcurrency must be > 0.", nameof(maxConcurrency));
        _maxConcurrency = maxConcurrency;
        return this;
    }

    /// <summary>
    /// Sets the number of retry attempts on error.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithRetryCountOnError(int retryCountOnError)
    {
        if (retryCountOnError < 0) throw new ArgumentException("RetryCountOnError must be >= 0.", nameof(retryCountOnError));
        _retryCountOnError = retryCountOnError;
        return this;
    }

    /// <summary>
    /// Configures no retries on error.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithNoRetries() => WithRetryCountOnError(0);

    /// <summary>
    /// Configures infinite retries on error.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithInfiniteRetries() => WithRetryCountOnError(int.MaxValue);

    // Consumption

    /// <summary>
    /// Sets the maximum batch size for database polling.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithMaxBatchSize(int maxBatchSize)
    {
        if (maxBatchSize <= 0) throw new ArgumentException("MaxBatchSize must be > 0.", nameof(maxBatchSize));
        _maxBatchSize = maxBatchSize;
        return this;
    }

    /// <summary>
    /// Sets the maximum processing iterations. -1 means unlimited (greedy mode).
    /// </summary>
    public OutboxConsumerSettingsBuilder WithMaxProcessingIterations(int maxProcessingIterations)
    {
        if (maxProcessingIterations < -1) throw new ArgumentException("MaxProcessingIterations must be >= -1.", nameof(maxProcessingIterations));
        _maxProcessingIterations = maxProcessingIterations;
        return this;
    }

    /// <summary>
    /// Configures single-iteration processing.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithSingleIteration() => WithMaxProcessingIterations(1);

    /// <summary>
    /// Configures unlimited iterations (greedy mode).
    /// </summary>
    public OutboxConsumerSettingsBuilder WithUnlimitedIterations() => WithMaxProcessingIterations(-1);

    /// <summary>
    /// Sets the delay between processing iterations.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithIterationDelay(TimeSpan iterationDelay)
    {
        if (iterationDelay.Ticks < 0) throw new ArgumentException("IterationDelay must be >= TimeSpan.Zero.", nameof(iterationDelay));
        _iterationDelay = iterationDelay;
        return this;
    }

    /// <summary>
    /// Sets the message lock duration.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithLockDuration(TimeSpan lockDuration)
    {
        if (lockDuration < TimeSpan.Zero) throw new ArgumentException("LockDuration must be >= TimeSpan.Zero.", nameof(lockDuration));
        _lockDuration = lockDuration;
        return this;
    }

    /// <summary>
    /// Disables lock duration — messages are not locked before processing.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithNoLockDuration() => WithLockDuration(TimeSpan.Zero);

    /// <summary>
    /// Sets the lock renewal time. Must be less than <see cref="LockDuration"/>.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithLockRenewal(TimeSpan lockRenewal)
    {
        if (lockRenewal.Ticks < 0) throw new ArgumentException("LockRenewal must be >= TimeSpan.Zero.", nameof(lockRenewal));
        _lockRenewal = lockRenewal;
        return this;
    }

    /// <summary>
    /// Sets the lookback interval for selecting messages.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithLookbackInterval(TimeSpan lookbackInterval)
    {
        if (lookbackInterval.Ticks <= 0) throw new ArgumentException("LookbackInterval must be > TimeSpan.Zero.", nameof(lookbackInterval));
        _lookbackInterval = lookbackInterval;
        return this;
    }

    /// <summary>
    /// Sets the maximum delivery attempts.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithMaxDeliveryAttempts(int maxDeliveryAttempts)
    {
        if (maxDeliveryAttempts <= 0) throw new ArgumentException("MaxDeliveryAttempts must be > 0.", nameof(maxDeliveryAttempts));
        _maxDeliveryAttempts = maxDeliveryAttempts;
        return this;
    }

    /// <summary>
    /// Sets the batching window for accumulating messages.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithBatchingWindow(TimeSpan batchingWindow)
    {
        if (batchingWindow.Ticks < 0) throw new ArgumentException("BatchingWindow must be >= TimeSpan.Zero.", nameof(batchingWindow));
        _batchingWindow = batchingWindow;
        return this;
    }

    /// <summary>
    /// Disables batching window — take whatever is available now.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithNoBatchingWindow() => WithBatchingWindow(TimeSpan.Zero);

    /// <summary>
    /// Sets the per-tenant processing timeout.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithPerTenantTimeout(TimeSpan perTenantTimeout)
    {
        if (perTenantTimeout.Ticks < 0) throw new ArgumentException("PerTenantTimeout must be >= TimeSpan.Zero.", nameof(perTenantTimeout));
        _perTenantTimeout = perTenantTimeout;
        return this;
    }

    /// <summary>
    /// Sets the maximum degree of tenant parallelism.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithPerTenantMaxDegreeOfParallelism(int degree)
    {
        if (degree == 0) throw new ArgumentException("PerTenantMaxDegreeOfParallelism cannot be 0.", nameof(degree));
        _perTenantMaxDegreeOfParallelism = degree;
        return this;
    }

    /// <summary>
    /// Configures sequential processing (no parallelism).
    /// </summary>
    public OutboxConsumerSettingsBuilder WithSequentialProcessing() => WithPerTenantMaxDegreeOfParallelism(1);

    /// <summary>
    /// Configures parallel processing using all available processors.
    /// </summary>
    public OutboxConsumerSettingsBuilder WithMaxParallelism() => WithPerTenantMaxDegreeOfParallelism(-1);

    // Lifecycle

    /// <summary>
    /// Marks the consumer group as paused.
    /// </summary>
    public OutboxConsumerSettingsBuilder Paused(bool paused = true)
    {
        _paused = paused;
        return this;
    }

    /// <summary>
    /// Convenience overload to explicitly set resumed state.
    /// </summary>
    public OutboxConsumerSettingsBuilder Resumed() => Paused(false);
}
