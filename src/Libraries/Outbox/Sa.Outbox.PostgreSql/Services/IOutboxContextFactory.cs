using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Services;

public interface IOutboxContextFactory
{
    IOutboxContextOperations<TMessage> Create<TMessage>(OutboxDeliveryMessage<TMessage> delivery);
}
