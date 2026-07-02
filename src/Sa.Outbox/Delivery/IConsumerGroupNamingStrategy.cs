namespace Sa.Outbox.Delivery;

/// <summary>
/// Determines the consumer group name used by Kafka-style or PostgreSQL-based consumer groups
/// for a given consumer type. Allows custom naming conventions beyond the default type-based naming.
/// </summary>
public interface IConsumerGroupNamingStrategy
{
    /// <summary>
    /// Returns the consumer group name for the specified consumer type.
    /// </summary>
    /// <typeparam name="TConsumer">The consumer type to generate a group name for.</typeparam>
    /// <returns>A human-readable consumer group identifier.</returns>
    string GetConsumerGroupName<TConsumer>();
}

