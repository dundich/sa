using System.Diagnostics.CodeAnalysis;
using Sa.Outbox.Delivery;

namespace Sa.Outbox;

public interface IOutboxBuilder
{
    /// <summary>
    /// Gets the current publish settings for the outbox configuration.
    /// </summary>
    OutboxPublishSettings PublishSettings { get; }

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
    IOutboxBuilder WithTenantSettings(Action<IServiceProvider, TenantSettings> configure);

    /// <summary>
    /// Registers a custom implementation of IDeliveryBatcher to control how messages are batched for delivery.
    /// </summary>
    IOutboxBuilder WithDeliveryBatcher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
         where TImplementation : class, IDeliveryBatcher;
}
