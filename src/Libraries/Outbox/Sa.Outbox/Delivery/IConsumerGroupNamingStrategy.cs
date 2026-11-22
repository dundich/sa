namespace Sa.Outbox.Delivery;

/// <summary>
/// Determines the consumer group name used by Kafka-style or PostgreSQL-based consumer groups
/// for a given consumer type. Allows custom naming conventions beyond the default type-based naming.
/// </summary>
/// <remarks>
/// The returned name is a group identity, not a label: it is used verbatim as the consumer's DI key,
/// the settings key, the job name and part of the PostgreSQL partition name. It is therefore
/// validated like an explicit <c>consumerGroupId</c> and must contain only lowercase ASCII letters,
/// digits and underscores — a value such as <c>Orders-V2</c> throws
/// <see cref="ArgumentException"/> at registration instead of being rewritten to <c>orders_v2</c>.
/// </remarks>
public interface IConsumerGroupNamingStrategy
{
    /// <summary>
    /// Returns the consumer group name for the specified consumer type.
    /// </summary>
    /// <remarks>
    /// The caller validates the returned name and throws <see cref="ArgumentException"/> when it is
    /// not canonical, so an implementation does not need to check it — but it must produce a name
    /// that passes.
    /// </remarks>
    /// <typeparam name="TConsumer">The consumer type to generate a group name for.</typeparam>
    /// <returns>
    /// A consumer group identifier that is already canonical: lowercase ASCII letters, digits and
    /// underscores only.
    /// </returns>
    string GetConsumerGroupName<TConsumer>();
}

