using Sa.Outbox.Repository;
using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Delivers a batch of messages with error handling and retry mechanisms
/// </summary>
internal interface IDeliveryCourier
{
    ValueTask<int> Deliver<TMessage>(
        ConsumeSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> outboxMessages,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage;
}
