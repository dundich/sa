namespace Sa.Outbox;


/// <summary>
/// Indicates that this is a configuration for message delivery in the Outbox.
/// </summary>
public sealed class ConsumerGroupSettings(string consumerGroupId, bool isSingleton)
{
    /// <summary>
    /// Group identity for consuming
    /// </summary>
    public string ConsumerGroupId => consumerGroupId;

    public bool IsSingleton => isSingleton;

    /// <summary>
    /// Gets the scheduling settings for the delivery job.
    /// </summary>
    public ScheduleSettings ScheduleSettings { get; } = new();

    /// <summary>
    /// Gets the consumption settings for processing messages.
    /// </summary>
    public ConsumeSettings ConsumeSettings { get; } = new();
}
