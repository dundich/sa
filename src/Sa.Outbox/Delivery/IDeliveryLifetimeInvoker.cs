namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages using a consumer in scope
/// </summary>
internal interface IDeliveryLifetimeInvoker
{
    Task ConsumeInScope<TMessage>(
        ConsumerGroupSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken);
}
