namespace Sa.Outbox;

/// <summary>
/// Represents the consumption settings for retrieving & processing messages from the Outbox
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ConsumeSettings"/> class.
/// </remarks>
/// <param name="consumerGroupId">Group identity for consuming. If null or empty, uses default.</param>
public sealed class ConsumeSettings(string? consumerGroupId = null)
{

    /// <summary>
    /// Group identity for consuming
    /// </summary>
    public string ConsumerGroupId { get; } = consumerGroupId ?? string.Empty;

    /// <summary>
    /// Maximum number of processing iterations when greedy mode is enabled.
    /// -1 means unlimited iterations.
    /// </summary>
    public int MaxProcessingIterations { get; set; } = 10;

    /// <summary>
    /// Delay between processing iterations when in greedy mode.
    /// Helps prevent tight-looping when there are no messages.
    /// </summary>
    public TimeSpan IterationDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets the maximum size of the Outbox message batch for each database poll.
    /// Recommended values: 16, 32, 64, 128, 256, 512, 1024 ...
    /// </summary>
    public int MaxBatchSize { get; set; } = 16;

    /// <summary>
    /// Message lock expiration time. 
    /// When a batch of messages for a bus instance is acquired, the messages will be locked (reserved) for that amount of time.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long before <see cref="LockDuration"/> to request a lock renewal. 
    /// This should be much shorter than <see cref="LockDuration"/>.
    /// </summary>
    public TimeSpan LockRenewal { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Select outbox messages for processing for the period
    /// </summary>
    public TimeSpan LookbackInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The maximum number of delivery attempts before delivery will not be attempted again.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>
    /// The maximum number of messages that can take in part
    /// <seealso cref="MaxBatchSize">default value</seealso>
    /// </summary>
    public int? ConsumeBatchSize { get; set; }

    /// <summary>
    /// Time window to accumulate messages before processing a batch.
    /// Delay to wait for "full set of messages" from input messages
    /// </summary>
    public TimeSpan BatchingWindow { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Maximum processing time allowed for each individual tenant.
    /// If processing exceeds this timeout, it will be cancelled and tenant marked as failed.
    /// </summary>
    public TimeSpan PerTenantTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum number of tenants to process in parallel.
    /// Values:
    /// - 0 or 1: Sequential processing (default)
    /// - > 1: Parallel processing with specified degree
    /// - -1: Use Environment.ProcessorCount
    /// </summary>
    public int PerTenantMaxDegreeOfParallelism { get; set; } = 1;
}


/// <summary>
/// Extension methods for fluent configuration of <see cref="ConsumeSettings"/>.
/// </summary>
public static class ConsumeSettingsExtensions
{
    /// <summary>
    /// Sets the maximum batch size for database polling.
    /// </summary>
    public static ConsumeSettings WithMaxBatchSize(
        this ConsumeSettings settings,
        int size)
    {
        if (size <= 0)
            throw new ArgumentException("Batch size must be positive", nameof(size));

        settings.MaxBatchSize = size;
        return settings;
    }

    /// <summary>
    /// Sets the message lock duration.
    /// </summary>
    public static ConsumeSettings WithLockDuration(
        this ConsumeSettings settings,
        TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentException("Lock duration cannot be negative", nameof(duration));

        settings.LockDuration = duration;
        return settings;
    }

    /// <summary>
    /// Disables message locking.
    /// </summary>
    public static ConsumeSettings WithNoLockDuration(this ConsumeSettings settings)
    {
        settings.LockDuration = TimeSpan.Zero;
        return settings;
    }

    /// <summary>
    /// Sets the lock renewal time.
    /// </summary>
    public static ConsumeSettings WithLockRenewal(
        this ConsumeSettings settings,
        TimeSpan renewal)
    {
        if (renewal < TimeSpan.Zero)
            throw new ArgumentException("Lock renewal cannot be negative", nameof(renewal));

        settings.LockRenewal = renewal;
        return settings;
    }

    /// <summary>
    /// Sets the lookback interval for selecting messages.
    /// </summary>
    public static ConsumeSettings WithLookbackInterval(
        this ConsumeSettings settings,
        TimeSpan interval)
    {
        if (interval < TimeSpan.Zero)
            throw new ArgumentException("Lookback interval cannot be negative", nameof(interval));

        settings.LookbackInterval = interval;
        return settings;
    }

    /// <summary>
    /// Sets the batching window for accumulating messages.
    /// </summary>
    public static ConsumeSettings WithBatchingWindow(
        this ConsumeSettings settings,
        TimeSpan window)
    {
        if (window < TimeSpan.Zero)
            throw new ArgumentException("Batching window cannot be negative", nameof(window));

        settings.BatchingWindow = window;
        return settings;
    }

    /// <summary>
    /// Disables batching window.
    /// </summary>
    public static ConsumeSettings WithNoBatchingWindow(this ConsumeSettings settings)
    {
        settings.BatchingWindow = TimeSpan.Zero;
        return settings;
    }

    /// <summary>
    /// Sets the maximum delivery attempts.
    /// </summary>
    public static ConsumeSettings WithMaxDeliveryAttempts(
        this ConsumeSettings settings,
        int attempts)
    {
        if (attempts < 0)
            throw new ArgumentException("Delivery attempts cannot be negative", nameof(attempts));

        settings.MaxDeliveryAttempts = attempts;
        return settings;
    }

    /// <summary>
    /// Sets the consume batch size.
    /// </summary>
    public static ConsumeSettings WithConsumeBatchSize(
        this ConsumeSettings settings,
        int? batchSize)
    {
        if (batchSize.HasValue && batchSize.Value <= 0)
            throw new ArgumentException("Consume batch size must be positive", nameof(batchSize));

        settings.ConsumeBatchSize = batchSize;
        return settings;
    }

    /// <summary>
    /// Sets the per-tenant processing timeout.
    /// </summary>
    public static ConsumeSettings WithTenantTimeout(
        this ConsumeSettings settings,
        TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentException("Timeout cannot be negative", nameof(timeout));

        settings.PerTenantTimeout = timeout;
        return settings;
    }

    /// <summary>
    /// Sets the maximum processing iterations.
    /// </summary>
    public static ConsumeSettings WithMaxProcessingIterations(
        this ConsumeSettings settings,
        int iterations)
    {
        settings.MaxProcessingIterations = Math.Max(-1, iterations);
        return settings;
    }

    /// <summary>
    /// Configures for single iteration processing.
    /// </summary>
    public static ConsumeSettings WithSingleIteration(this ConsumeSettings settings)
    {
        settings.MaxProcessingIterations = 1;
        return settings;
    }

    /// <summary>
    /// Configures for unlimited iterations.
    /// </summary>
    public static ConsumeSettings WithUnlimitedIterations(this ConsumeSettings settings)
    {
        settings.MaxProcessingIterations = -1;
        return settings;
    }

    /// <summary>
    /// Sets the delay between processing iterations.
    /// </summary>
    public static ConsumeSettings WithIterationDelay(
        this ConsumeSettings settings,
        TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentException("Iteration delay cannot be negative", nameof(delay));

        settings.IterationDelay = delay;
        return settings;
    }

    /// <summary>
    /// Configures sequential processing (no parallelism).
    /// </summary>
    public static ConsumeSettings WithTenantSequentialProcessing(this ConsumeSettings settings)
    {
        settings.PerTenantMaxDegreeOfParallelism = 1;
        return settings;
    }

    /// <summary>
    /// Configures parallel processing using all available processors.
    /// </summary>
    public static ConsumeSettings WithTenantMaxParallelism(this ConsumeSettings settings)
    {
        settings.PerTenantMaxDegreeOfParallelism = -1;
        return settings;
    }

    /// <summary>
    /// Configures parallel processing with specific degree.
    /// </summary>
    public static ConsumeSettings WithTenantParallelProcessing(
        this ConsumeSettings settings,
        int degree)
    {
        if (degree <= 0 && degree != -1)
            throw new ArgumentException("Parallelism degree must be positive or -1", nameof(degree));

        settings.PerTenantMaxDegreeOfParallelism = degree;
        return settings;
    }
}