using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

public interface IDeliveryProcessor
{
    Task<long> ProcessMessages<TMessage>(OutboxDeliverySettings settings, CancellationToken cancellationToken)
        where TMessage: IOutboxPayloadMessage;
}
