
namespace Sa.Outbox.Delivery;

public interface IScopedConsumer
{
    Task MessageProcessingAsync<TMessage>(IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages, CancellationToken cancellationToken);
}
