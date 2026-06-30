using Sa.Outbox.Delivery;
using Sa.Outbox.Metadata;
using Sa.Outbox.Partitional;
using Sa.Outbox.Publication;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox;

public interface IOutboxBuilder
{
    /// <summary>
    /// Configures publish settings for the outbox.
    /// </summary>
    /// <param name="configure">An action to configure <see cref="OutboxPublishSettings"/> within the service provider scope.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithPublishSettings(Action<IServiceProvider, OutboxPublishSettings> configure);

    /// <summary>
    /// Configures the delivery settings for the outbox.
    /// </summary>
    /// <param name="build">An action to configure the delivery settings.</param>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder WithDeliveries(Action<IDeliveryBuilder> build);

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
        Func<TMessage, string>? getPayloadId = null) where TMessage : class
            => WithMetadata((_, m) => m.AddMetadata<TMessage>(partName, getPayloadId));

    /// <summary>
    /// Registers metadata for a message type that implements <see cref="IOutboxPublishable"/>,
    /// automatically deriving the part name and payload ID resolver from the type itself.
    /// </summary>
    /// <typeparam name="TMessage">A message type implementing <see cref="IOutboxPublishable"/>.</typeparam>
    /// <returns>The same <see cref="IOutboxBuilder"/> instance for chaining.</returns>
    IOutboxBuilder AddMetadata<TMessage>() where TMessage : class, IOutboxPublishable
        => WithMetadata((_, m) => m.AddMetadata<TMessage>());
}
