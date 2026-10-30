namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes outbox messages in batches until all pending messages are delivered or cancellation is requested.
/// Implements a continuous polling pattern to ensure reliable message delivery.
/// </summary>
public interface IDeliveryProcessor
{
    /// <summary>
    /// Runs the delivery loop for one consumer group.
    /// </summary>
    /// <returns>
    /// The number of messages handled — every message the loop rented and gave a final status to,
    /// whether the consumer succeeded it, left it for a retry, or dead-lettered it. The loop itself
    /// keeps iterating while messages are being rented.
    /// </returns>
    Task<long> ProcessMessages<TMessage>(OutboxConsumerSettings settings, CancellationToken cancellationToken);
}
