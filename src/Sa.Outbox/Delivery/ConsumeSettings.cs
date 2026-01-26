namespace Sa.Outbox.Delivery;

/// <summary>
/// Represents the consumption settings for retrieving & processing messages from the Outbox
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ConsumeSettings"/> class.
/// </remarks>
/// <param name="consumerGroupId">Group identity for consuming. If null or empty, uses default.</param>
public sealed class ConsumeSettings
{
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
