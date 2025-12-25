namespace Sa.Outbox.Repository;

public interface IOutboxContextFactory
{
    IOutboxContextOperations<TMessage> Create<TMessage>(OutboxDeliveryMessage<TMessage> delivery);
}
