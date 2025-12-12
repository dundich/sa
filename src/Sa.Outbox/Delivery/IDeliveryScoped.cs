using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages using a consumer in scope
/// </summary>
internal interface IDeliveryScoped
{
    Task Consume<TMessage>(
        ConsumeSettings settings,
        IReadOnlyCollection<IOutboxContextOperations<TMessage>> outboxMessages, 
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage;
}
