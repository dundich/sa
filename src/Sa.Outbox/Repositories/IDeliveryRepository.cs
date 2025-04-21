﻿namespace Sa.Outbox;


/// <summary>
/// needed External implementation
/// </summary>
public interface IDeliveryRepository
{
    /// <summary>
    /// Exclusively take for processing for the client
    /// </summary>
    Task<int> StartDelivery<TMessage>(Memory<OutboxDeliveryMessage<TMessage>> writeBuffer, int batchSize, TimeSpan lockDuration, OutboxMessageFilter filter, CancellationToken cancellationToken);
    /// <summary>
    /// Complete the delivery
    /// </summary>
    Task<int> FinishDelivery<TMessage>(IOutboxContext<TMessage>[] outboxMessages, OutboxMessageFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Extend the delivery (retain the lock for the client)
    /// </summary>
    Task<int> ExtendDelivery(TimeSpan lockExpiration, OutboxMessageFilter filter, CancellationToken cancellationToken);
}
