namespace Sa.Outbox;


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
