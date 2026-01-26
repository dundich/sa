using Sa.Outbox.Support;
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
    /// <param name="consumerGroupId">Group identity for consuming.</param>
    /// <param name="configure">An optional action to configure the delivery settings.</param>
    /// <returns>The delivery builder instance.</returns>
    IDeliveryBuilder AddDeliveryScoped<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, ConsumerGroupSettings>? configure = null
    )
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : IOutboxPayloadMessage;

    /// <summary>
    /// Adds singleton delivery for the specified consumer and message type.
    /// </summary>
    IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(
        string consumerGroupId,
        Action<IServiceProvider, ConsumerGroupSettings>? configure = null
    )
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : IOutboxPayloadMessage;


    /// <summary>
    /// Added provider functionality to dynamically calculate batch sizes for delivery
    /// </summary>
    IDeliveryBuilder AddDeliveryBatching<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>()
            where TImplementation : class, IDeliveryBatcher;

}
