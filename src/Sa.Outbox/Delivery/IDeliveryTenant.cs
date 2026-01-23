using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal interface IDeliveryTenant
{
    Task<int> ProcessInTenant<TMessage>(
            int tenantId,
            ConsumerGroupSettings settings,
            CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage;
}
