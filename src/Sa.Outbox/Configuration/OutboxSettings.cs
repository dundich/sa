namespace Sa.Outbox;


/// <summary>
/// Settings for publishing messages from the Outbox.
/// </summary>
public class OutboxPublishSettings
{
    /// <summary>
    /// The maximum batch size of messages to be sent at once.
    /// Default value: 16.
    /// for array pool size: 16, 32, 64, 128, 256, 512, 1024, 2048, 4096
    /// </summary>
    public int MaxBatchSize { get; set; } = 64;
}


/// <summary>
/// Indicates that this is a configuration for message delivery in the Outbox.
/// </summary>
public class OutboxDeliverySettings(Guid jobId, int instanceIndex = 0)
{
    /// <summary>
    ///  Gets the unique identifier for the delivery job
    /// </summary>
    public Guid JobId => jobId;
    /// <summary>
    /// Indicates the index of the worker instance.
    /// </summary>
    public int WorkerInstanceIndex => instanceIndex;
    /// <summary>
    /// Gets the scheduling settings for the delivery job.
    /// </summary>
    public ScheduleSettings ScheduleSettings { get; } = new();
    /// <summary>
    /// Gets the extraction settings for retrieving messages from the Outbox.
    /// </summary>
    public ExtractSettings ExtractSettings { get; } = new();
    /// <summary>
    /// Gets the consumption settings for processing messages.
    /// </summary>
    public ConsumeSettings ConsumeSettings { get; } = new();
}

/// <summary>
/// Represents the scheduling settings for the delivery job.
/// </summary>
public class ScheduleSettings
{
    public string? Name { get; set; }
    public TimeSpan ExecutionInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// job schedule delay before start
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(10);

    public int RetryCountOnError { get; set; } = 2;
}

/// <summary>
/// Represents the extraction settings for retrieving messages from the Outbox.
/// </summary>
public class ExtractSettings
{
    /// <summary>
    /// Gets or sets the maximum size of the Outbox message batch for each database poll.
    /// for array pool size: 16, 32, 64, 128, 256, 512, 1024 ...
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
    /// Repeat extract for each tenant
    /// <seealso cref="IOutboxBuilder.WithPartitioningSupport"/>
    /// </summary>
    public bool ForEachTenant { get; set; }

    /// <summary>
    /// select outbox messages for processing for the period
    /// </summary>
    public TimeSpan LookbackInterval { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Represents the consumption settings for processing messages from the Outbox.
/// </summary>
public class ConsumeSettings
{
    /// <summary>
    /// The maximum number of delivery attempts before delivery will not be attempted again.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 3;
    /// <summary>
    /// The maximum number of messages that can take in part
    /// <seealso cref="ExtractSettings.MaxBatchSize">default value</seealso>
    /// </summary>
    public int? ConsumeBatchSize { get; set; }
}
