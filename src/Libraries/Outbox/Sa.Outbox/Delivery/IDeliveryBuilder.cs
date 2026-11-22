using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Delivery;


/// <summary>
/// Represents a builder for creating outbox deliveries.
/// </summary>
public partial interface IDeliveryBuilder
{
    /// <summary>
    /// Adds scoped delivery for the specified consumer and message type.
    /// </summary>
    /// <typeparam name="TConsumer">The type of consumer.</typeparam>
    /// <typeparam name="TMessage">The type of message.</typeparam>
    /// <param name="consumerGroupId">
    /// Group identity for consuming. It is used verbatim (never rewritten), so it must contain only
    /// lowercase ASCII letters, digits and underscores; a value such as <c>es-group</c> is rejected
    /// rather than silently collapsed into <c>es_group</c>.
    /// </param>
    /// <param name="configure">An optional action to configure the delivery settings via builder.</param>
    /// <param name="jobId">An optional job identifier to bind this delivery to a specific scheduled job.</param>
    /// <param name="configurationKey">
    /// An optional configuration section name to bind this group's settings from. When supplied,
    /// non-null values from that section override <c>OutboxDefaults</c>; when absent, the
    /// <c>Outbox:DeliveryGroups:{group}</c> convention applies (see
    /// <see cref="OutboxDeliveryConfiguration"/>), and an explicit empty string opts the group out.
    /// The <paramref name="configure"/> callback still has the final word.
    /// </param>
    /// <returns>The delivery builder instance.</returns>
    /// <exception cref="ArgumentException"><paramref name="consumerGroupId"/> is blank or contains a
    /// character outside <c>[a-z0-9_]</c>.</exception>
    IDeliveryBuilder AddDeliveryScoped<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null,
        string? configurationKey = null
    ) where TConsumer : class, IConsumer<TMessage>;

    /// <summary>
    /// Adds scoped delivery for the specified consumer and message type without an explicit consumer group ID.
    /// A default group name is derived from <typeparamref name="TConsumer"/> using the configured naming strategy.
    /// </summary>
    /// <typeparam name="TConsumer">The type of consumer.</typeparam>
    /// <typeparam name="TMessage">The type of message.</typeparam>
    /// <param name="configure">An optional action to configure the delivery settings via builder.</param>
    /// <param name="jobId">An optional job identifier to bind this delivery to a specific scheduled job.</param>
    /// <param name="configurationKey">
    /// An optional configuration section name to bind this group's settings from. When absent, the
    /// <c>Outbox:DeliveryGroups:{group}</c> convention applies (see
    /// <see cref="OutboxDeliveryConfiguration"/>); an explicit empty string opts the group out.
    /// The <paramref name="configure"/> callback still has the final word.
    /// </param>
    /// <returns>The delivery builder instance.</returns>
    IDeliveryBuilder AddDeliveryScoped<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null,
        string? configurationKey = null
    ) where TConsumer : class, IConsumer<TMessage>;

    /// <summary>
    /// Adds singleton delivery for the specified consumer and message type with an explicit consumer group ID.
    /// </summary>
    /// <typeparam name="TConsumer">The type of consumer.</typeparam>
    /// <typeparam name="TMessage">The type of message.</typeparam>
    /// <param name="consumerGroupId">
    /// Group identity for consuming. It is used verbatim (never rewritten), so it must contain only
    /// lowercase ASCII letters, digits and underscores; a value such as <c>es-group</c> is rejected
    /// rather than silently collapsed into <c>es_group</c>.
    /// </param>
    /// <param name="configure">An optional action to configure the delivery settings via builder.</param>
    /// <param name="jobId">An optional job identifier to bind this delivery to a specific scheduled job.</param>
    /// <param name="configurationKey">
    /// An optional configuration section name to bind this group's settings from. When supplied,
    /// non-null values from that section override <c>OutboxDefaults</c>; when absent, the
    /// <c>Outbox:DeliveryGroups:{group}</c> convention applies (see
    /// <see cref="OutboxDeliveryConfiguration"/>), and an explicit empty string opts the group out.
    /// The <paramref name="configure"/> callback still has the final word.
    /// </param>
    /// <returns>The delivery builder instance.</returns>
    /// <exception cref="ArgumentException"><paramref name="consumerGroupId"/> is blank or contains a
    /// character outside <c>[a-z0-9_]</c>.</exception>
    IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null,
        string? configurationKey = null
    ) where TConsumer : class, IConsumer<TMessage>;

    /// <summary>
    /// Adds singleton delivery for the specified consumer and message type without an explicit consumer group ID.
    /// A default group name is derived from <typeparamref name="TConsumer"/> using the configured naming strategy.
    /// </summary>
    /// <typeparam name="TConsumer">The type of consumer.</typeparam>
    /// <typeparam name="TMessage">The type of message.</typeparam>
    /// <param name="configure">An optional action to configure the delivery settings via builder.</param>
    /// <param name="jobId">An optional job identifier to bind this delivery to a specific scheduled job.</param>
    /// <param name="configurationKey">
    /// An optional configuration section name to bind this group's settings from. When absent, the
    /// <c>Outbox:DeliveryGroups:{group}</c> convention applies (see
    /// <see cref="OutboxDeliveryConfiguration"/>); an explicit empty string opts the group out.
    /// The <paramref name="configure"/> callback still has the final word.
    /// </param>
    /// <returns>The delivery builder instance.</returns>
    IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        Action<IServiceProvider, OutboxConsumerSettingsBuilder>? configure = null,
        Guid? jobId = null,
        string? configurationKey = null
    ) where TConsumer : class, IConsumer<TMessage>;

    /// <summary>
    /// Sets the default naming strategy used to derive consumer group names when no explicit <c>consumerGroupId</c> is provided.
    /// </summary>
    /// <param name="strategy">The naming strategy implementation to use.</param>
    /// <returns>The delivery builder instance.</returns>
    IDeliveryBuilder ConfigureDefaultNamingStrategy(IConsumerGroupNamingStrategy strategy);
}
