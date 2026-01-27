using Sa.Outbox.Delivery;
using Sa.Outbox.Metadata;
using Sa.Outbox.Publication;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox;

public interface IOutboxBuilder
{
    /// <summary>
    /// Configure publish settings for the outbox.
    /// </summary>
    IOutboxBuilder WithPublishSettings(Action<IServiceProvider, OutboxPublishSettings> configure);

    /// <summary>
    /// Configures the delivery settings for the outbox.
    /// </summary>
    /// <param name="build">An action to configure the delivery settings.</param>
    /// <returns>The current instance of the IOutboxSettingsBuilder.</returns>
    IOutboxBuilder WithDeliveries(Action<IDeliveryBuilder> build);

    /// <summary>
    /// Enables partitioning support for the outbox.
    /// </summary>
    /// <param name="configure">An action to configure the partitioning settings.</param>
    /// <returns>The current instance of the IOutboxSettingsBuilder.</returns>
    IOutboxBuilder WithTenants(Action<IServiceProvider, TenantSettings> configure);

    /// <summary>
    /// Registers a custom implementation of IDeliveryBatcher to control how messages are batched for delivery.
    /// </summary>
    IOutboxBuilder WithDeliveryBatcher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
         where TImplementation : class, IDeliveryBatcher;


    IOutboxBuilder WithMetadata(Action<IServiceProvider, IOutboxMessageMetadataBuilder> configure);
}
