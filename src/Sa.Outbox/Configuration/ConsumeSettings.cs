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
    /// Gets or sets the maximum size of the Outbox message batch for each database poll.
    /// for array pool size: 16, 32, 64, 128, 256, 512, 1024 ...
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
    /// Delay to wait for "full set of messages"
    /// </summary>
    public TimeSpan ProcessingDelay { get; private set; } = TimeSpan.FromSeconds(3);


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

    public ConsumeSettings WithProcessingDelay(TimeSpan interval)
    {
        ProcessingDelay = interval;
        return this;
    }

    public ConsumeSettings WithNoProcessingDelay()
    {
        ProcessingDelay = TimeSpan.Zero;
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
}
