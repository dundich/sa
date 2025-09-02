using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

public interface IDeliveryCourier
{
    ValueTask<int> Deliver<TMessage>(
        IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages, 
        int maxDeliveryAttempts, 
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage;
}
