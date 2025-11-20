using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Orchestrates message retrieval from repository and delivery to consumers with retry mechanisms and multi-tenant support.
/// </summary>
internal interface IDeliveryRelay
{
    Task<int> StartDelivery<TMessage>(OutboxDeliverySettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage;
}
