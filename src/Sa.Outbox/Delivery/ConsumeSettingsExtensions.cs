namespace Sa.Outbox.Delivery;

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
