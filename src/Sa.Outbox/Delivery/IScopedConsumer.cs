using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

internal interface IScopedConsumer
{
    Task MessageProcessingAsync<TMessage>(
        IReadOnlyCollection<IOutboxContext<TMessage>> outboxMessages, 
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage;
}
