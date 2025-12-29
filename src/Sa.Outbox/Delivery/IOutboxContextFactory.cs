namespace Sa.Outbox.Delivery;

public interface IOutboxContextFactory
{
    IOutboxContextOperations<TMessage> Create<TMessage>(OutboxDeliveryMessage<TMessage> deliveryMessage);
}
