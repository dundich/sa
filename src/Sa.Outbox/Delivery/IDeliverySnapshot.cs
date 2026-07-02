namespace Sa.Outbox.Delivery;

/// <summary>
/// Provides a read-only view of currently registered delivery parts and their associated consumer settings.
/// Useful for diagnostics, health checks, and runtime introspection of active outbox consumers.
/// </summary>
public interface IDeliverySnapshot
{
    /// <summary>
    /// Gets the distinct part names currently served by this snapshot.
    /// </summary>
    string[] Parts { get; }

    /// <summary>
    /// Gets the consumer settings arrays corresponding to each part in <see cref="Parts"/>.
    /// </summary>
    OutboxConsumerSettings[] ConsumerSettings { get; }

    /// <summary>
    /// Returns the distinct consumer group IDs registered in this snapshot.
    /// </summary>
    IEnumerable<string> GetConsumeGroupIds()
        => ConsumerSettings.Select(c => c.ConsumerGroupId).Distinct();
}
