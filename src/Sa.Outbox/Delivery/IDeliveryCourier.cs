using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Delivers a batch of messages with error handling and retry mechanisms
/// </summary>
internal interface IDeliveryCourier
{
    ValueTask<int> Deliver<TMessage>(
        IReadOnlyCollection<IOutboxContextOperations<TMessage>> outboxMessages,
        int maxDeliveryAttempts,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage;
}
