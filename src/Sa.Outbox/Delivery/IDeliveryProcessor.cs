using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes outbox messages in batches until all pending messages are delivered or cancellation is requested.
/// Implements a continuous polling pattern to ensure reliable message delivery.
/// </summary>
public interface IDeliveryProcessor
{
    Task<long> ProcessMessages<TMessage>(OutboxDeliverySettings settings, CancellationToken cancellationToken)
        where TMessage: IOutboxPayloadMessage;
}
