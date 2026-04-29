namespace Sa.Outbox.Delivery;

internal sealed class OutboxContextFactory(TimeProvider? timeProvider = null) : IOutboxContextFactory
{
    public IOutboxContextOperations<TMessage> Create<TMessage>(OutboxDeliveryMessage<TMessage> deliveryMessage)
    {
        return new OutboxContext<TMessage>(deliveryMessage, timeProvider ?? TimeProvider.System);
    }
}
