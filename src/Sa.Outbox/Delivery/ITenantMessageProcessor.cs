using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal interface ITenantMessageProcessor
{
    Task<int> ProcessTenantMessages<TMessage>(
        Memory<OutboxDeliveryMessage<TMessage>> buffer,
        ConsumeSettings settings, 
        int tenantId, 
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage;
}
