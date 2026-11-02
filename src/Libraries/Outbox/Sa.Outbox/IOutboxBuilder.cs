using Sa.Outbox.Delivery;
using Sa.Outbox.Metadata;
using Sa.Outbox.Partitional;
using Sa.Outbox.Publication;
using Sa.Outbox.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox;

public interface IOutboxBuilder
{
    /// <summary>
    /// Configures publish settings for this outbox scope.
    /// </summary>
    /// <remarks>
    /// <see cref="OutboxPublishSettings"/> is immutable, so the delegate receives the current
    /// instance and returns the updated one (<c>(_, settings) =&gt; settings.WithMaxBatchSize(1024)</c>).
    /// It runs once, when the settings are first resolved, and is keyed by the outbox scope: two
    /// providers in one container keep their own publish settings.
    /// </remarks>
    /// <param name="configure">Receives the current settings and returns the updated ones.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithPublishSettings(
        Func<IServiceProvider, OutboxPublishSettings, OutboxPublishSettings> configure);

    /// <summary>
    /// Configures the delivery settings for the outbox.
    /// </summary>
    /// <param name="build">An action to configure the delivery settings.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithDeliveries(Action<IDeliveryBuilder> build);

    /// <summary>
    /// Sets the <c>IConfiguration</c> section prefix a consumer group's settings are bound from when
    /// the group is registered without an explicit <c>configurationKey</c>.
    /// </summary>
    /// <remarks>
    /// The section is addressed as <c>{sectionPrefix}:{consumerGroupId}</c>, so with the default
    /// prefix (<see cref="OutboxDeliveryConfiguration.DefaultSectionPrefix"/>, <c>Outbox:DeliveryGroups</c>)
    /// group <c>orders</c> reads <c>Outbox:DeliveryGroups:orders</c>. An explicit
    /// <c>configurationKey</c> on <c>AddDelivery</c> still wins, and an explicit empty string opts that
    /// group out. Pass <see langword="null"/> to disable the convention for the whole application.
    /// A missing section is a no-op, so enabling the convention changes nothing unless the section exists.
    /// </remarks>
    /// <param name="sectionPrefix">The prefix, or <see langword="null"/> to disable the convention.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithDeliveryConfigurationSection(
        string? sectionPrefix = OutboxDeliveryConfiguration.DefaultSectionPrefix);

    /// <summary>
    /// Enables partitioning support for the outbox.
    /// </summary>
    /// <param name="configure">An action to configure the partitioning settings.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithTenants(Action<IServiceProvider, TenantSettings> configure);

    /// <summary>
    /// Registers a custom implementation of <see cref="IDeliveryBatcher"/> to control how messages are batched for delivery.
    /// </summary>
    /// <typeparam name="TImplementation">The custom batcher implementation to register.</typeparam>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithDeliveryBatcher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
         where TImplementation : class, IDeliveryBatcher;

    /// <summary>
    /// Registers a custom implementation of <see cref="IEmptyQueueWaitStrategy"/> that controls how
    /// long a consumer group waits between polls while its queue is empty.
    /// </summary>
    /// <typeparam name="TImplementation">The custom wait-strategy implementation to register.</typeparam>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithIdleWaitStrategy<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
         where TImplementation : class, IEmptyQueueWaitStrategy;

    /// <summary>
    /// Registers a specific <see cref="IEmptyQueueWaitStrategy"/> instance that controls how long a
    /// consumer group waits between polls while its queue is empty. Use this overload to configure a
    /// built-in strategy with non-default parameters, for example
    /// <c>new ExponentialIdleWaitStrategy(multiplier: 1.5, jitter: 0.3)</c>.
    /// </summary>
    /// <param name="strategy">The wait-strategy instance to register.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithIdleWaitStrategy(IEmptyQueueWaitStrategy strategy);

    /// <summary>
    /// Replaces the default JSON serializer with a factory that produces
    /// <see cref="IOutboxMessageSerializer"/> instances from the DI container. The registration is
    /// the single serialization strategy of the Outbox: whichever provider stores the message uses
    /// it for both publishing and delivery.
    /// </summary>
    /// <param name="messageSerializerFactory">Factory invoked to create the serializer instance.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithMessageSerializer(
        Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory);

    /// <summary>
    /// Registers a concrete serializer instance as the Outbox's <see cref="IOutboxMessageSerializer"/>.
    /// The instance is used directly without DI resolution.
    /// </summary>
    /// <typeparam name="TService">Concrete serializer type implementing <see cref="IOutboxMessageSerializer"/>.</typeparam>
    /// <param name="instance">Pre-created serializer instance.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer;

    /// <summary>
    /// Registers a serializer type as the Outbox's <see cref="IOutboxMessageSerializer"/>, resolved
    /// from DI as a singleton.
    /// </summary>
    /// <typeparam name="TService">Serializer type to register in DI.</typeparam>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithMessageSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IOutboxMessageSerializer;


    /// <summary>
    /// Configures message metadata registrations (part names and payload ID resolvers) for outbox message types.
    /// </summary>
    /// <param name="configure">An action to configure metadata through <see cref="IOutboxMessageMetadataBuilder"/>.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithMetadata(Action<IServiceProvider, IOutboxMessageMetadataBuilder> configure);

    /// <summary>
    /// Registers metadata for a message type using an explicit part name and optional payload ID resolver.
    /// </summary>
    /// <typeparam name="TMessage">The message type to register metadata for.</typeparam>
    /// <param name="partName">The logical partition name associated with the message type (e.g., <c>"orders"</c>).</param>
    /// <param name="getPayloadId">An optional delegate to extract the unique payload identifier from a message. Defaults to calling <c>GetPayloadId()</c> on <see cref="IOutboxPublishable"/>.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder AddMetadata<TMessage>(
        string partName,
        Func<TMessage, string>? getPayloadId = null)
            => WithMetadata((_, m) => m.AddMetadata<TMessage>(partName, getPayloadId));

    /// <summary>
    /// Registers metadata for a message type that implements <see cref="IOutboxPublishable"/>,
    /// automatically deriving the part name and payload ID resolver from the type itself.
    /// </summary>
    /// <typeparam name="TMessage">A message type implementing <see cref="IOutboxPublishable"/>.</typeparam>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder AddMetadata<TMessage>() where TMessage : IOutboxPublishable
        => WithMetadata((_, m) => m.AddMetadata<TMessage>());
}
