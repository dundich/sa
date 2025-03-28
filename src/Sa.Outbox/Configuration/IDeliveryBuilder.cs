using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox;


/// <summary>
/// Represents a builder for creating outbox deliveries.
/// </summary>
public interface IDeliveryBuilder
{
    /// <summary>
    /// Adds a delivery for the specified consumer and message type.
    /// </summary>
    /// <typeparam name="TConsumer">The type of consumer.</typeparam>
    /// <typeparam name="TMessage">The type of message.</typeparam>
    /// <param name="configure">An optional action to configure the delivery settings.</param>
    /// <param name="instanceCount">The number of instances to create for the delivery.</param>
    /// <returns>The delivery builder instance.</returns>
    IDeliveryBuilder AddDelivery<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsumer, TMessage>(Action<IServiceProvider, OutboxDeliverySettings>? configure = null, int instanceCount = 1)
        where TConsumer : class, IConsumer<TMessage>;
}