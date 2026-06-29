namespace Sa.Outbox.Delivery;

/// <summary>
/// Delivers a batch of messages with error handling and retry mechanisms
/// </summary>
internal interface IDeliveryCourier
{
    ValueTask<int> Deliver<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken);
}
