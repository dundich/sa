using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal interface ITenantMessageProcessor
{
    Task<int> ProcessTenantMessages<TMessage>(
        ConsumeSettings settings,
        Memory<OutboxDeliveryMessage<TMessage>> buffer,
        int tenantId,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage;
}
