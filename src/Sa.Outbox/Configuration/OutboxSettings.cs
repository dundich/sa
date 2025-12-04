namespace Sa.Outbox;


/// <summary>
/// Settings for publishing messages from the Outbox.
/// </summary>
public sealed class OutboxPublishSettings
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
public sealed class OutboxDeliverySettings(string? consumerGroupId = null)
{
    /// <summary>
    /// Gets the scheduling settings for the delivery job.
    /// </summary>
    public ScheduleSettings ScheduleSettings { get; } = new();
    /// <summary>
    /// Gets the consumption settings for processing messages.
    /// </summary>
    public ConsumeSettings ConsumeSettings { get; } = new(consumerGroupId);

}
