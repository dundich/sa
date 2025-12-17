namespace Sa.Outbox;

/// <summary>
/// Represents the consumption settings for retrieving & processing messages from the Outbox
/// </summary>
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
    public int MaxProcessingIterations { get; private set; } = 10;

    /// <summary>
    /// Delay between processing iterations when in greedy mode.
    /// Helps prevent tight-looping when there are no messages.
    /// </summary>
    public TimeSpan IterationDelay { get; private set; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets the maximum size of the Outbox message batch for each database poll.
    /// Recommended values: 16, 32, 64, 128, 256, 512, 1024 ...
    /// </summary>
    public int MaxBatchSize { get; private set; } = 16;

    /// <summary>
    /// Message lock expiration time. 
    /// When a batch of messages for a bus instance is acquired, the messages will be locked (reserved) for that amount of time.
    /// </summary>
    public TimeSpan LockDuration { get; private set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long before <see cref="LockDuration"/> to request a lock renewal. 
    /// This should be much shorter than <see cref="LockDuration"/>.
    /// </summary>
    public TimeSpan LockRenewal { get; private set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Select outbox messages for processing for the period
    /// </summary>
    public TimeSpan LookbackInterval { get; private set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The maximum number of delivery attempts before delivery will not be attempted again.
    /// </summary>
    public int MaxDeliveryAttempts { get; private set; } = 3;

    /// <summary>
    /// The maximum number of messages that can take in part
    /// <seealso cref="ConsumeSettings.MaxBatchSize">default value</seealso>
    /// </summary>
    public int? ConsumeBatchSize { get; private set; }

    /// <summary>
    /// Time window to accumulate messages before processing a batch.
    /// Delay to wait for "full set of messages" from input messages
    /// </summary>
    public TimeSpan BatchingWindow { get; private set; } = TimeSpan.FromSeconds(3);


    /// <summary>
    /// Maximum processing time allowed for each individual tenant.
    /// If processing exceeds this timeout, it will be cancelled and tenant marked as failed.
    /// </summary>
    public TimeSpan PerTenantTimeout { get; private set; } = TimeSpan.Zero;


    /// <summary>
    /// Maximum number of tenants to process in parallel.
    /// Values:
    /// - 0 or 1: Sequential processing (default)
    /// - > 1: Parallel processing with specified degree
    /// - -1: Use Environment.ProcessorCount
    /// </summary>
    public int PerTenantMaxDegreeOfParallelism { get; private set; } = 1;



    // Fluent 

    public ConsumeSettings WithMaxBatchSize(int size)
    {
        MaxBatchSize = size;
        return this;
    }

    public ConsumeSettings WithLockDuration(TimeSpan duration)
    {
        LockDuration = duration;
        return this;
    }

    public ConsumeSettings WithNoLockDuration()
    {
        LockDuration = TimeSpan.Zero;
        return this;
    }

    public ConsumeSettings WithLockRenewal(TimeSpan renewal)
    {
        LockRenewal = renewal;
        return this;
    }

    public ConsumeSettings WithLookbackInterval(TimeSpan interval)
    {
        LookbackInterval = interval;
        return this;
    }

    public ConsumeSettings WithBatchingWindow(TimeSpan interval)
    {
        BatchingWindow = interval;
        return this;
    }

    public ConsumeSettings WithNoBatchingWindow()
    {
        BatchingWindow = TimeSpan.Zero;
        return this;
    }

    public ConsumeSettings WithMaxDeliveryAttempts(int attempts)
    {
        MaxDeliveryAttempts = attempts;
        return this;
    }

    public ConsumeSettings WithConsumeBatchSize(int? batchSize)
    {
        ConsumeBatchSize = batchSize;
        return this;
    }

    public ConsumeSettings WithTenantTimeout(TimeSpan timeout)
    {
        PerTenantTimeout = timeout;
        return this;
    }

    public ConsumeSettings WithMaxProcessingIterations(int iterations)
    {
        MaxProcessingIterations = Math.Max(-1, iterations);
        return this;
    }

    public ConsumeSettings WithSingleIteration()
    {
        MaxProcessingIterations = 1;
        return this;
    }

    public ConsumeSettings WithIterationDelay(TimeSpan delay)
    {
        IterationDelay = delay;
        return this;
    }

    public ConsumeSettings WithPerTenanMaxDegreeOfParallelism(int degree)
    {
        PerTenantMaxDegreeOfParallelism = Math.Max(-1, degree);
        return this;
    }
}
