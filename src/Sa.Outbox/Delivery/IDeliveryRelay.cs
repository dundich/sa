using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

internal interface IDeliveryRelay
{
    Task<int> StartDelivery<TMessage>(OutboxDeliverySettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage;
}
