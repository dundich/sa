using Sa.Outbox.Delivery;

namespace Sa.Outbox.PlugServices;


/// <summary>
/// Manages outbox message delivery lifecycle including exclusive rent, return (acknowledge), and lock extension.
/// External implementations must provide the actual persistence and concurrency logic.
/// </summary>
public interface IOutboxDeliveryManager
{
    /// <summary>
    /// Exclusively take for processing for the client
    /// </summary>
    Task<int> RentDelivery<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Complete the delivery
    /// </summary>
    Task<int> ReturnDelivery<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extend the delivery (retain the lock for the client)
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pipeline calls this periodically while the batch is being processed (every
    /// <see cref="OutboxConsumerSettings.LockRenewal"/>) and stops the batch when the lease is no
    /// longer held, so the returned count is part of the contract, not diagnostics.
    /// </para>
    /// <para>
    /// Return the number of messages whose lease was actually extended. <c>0</c> means the lease is
    /// gone — the batch was re-rented by another worker, its transaction id was overwritten, or its
    /// entries are no longer pending — and the pipeline reports it as a lost lock. An
    /// implementation that returns <c>0</c> for "nothing to do" would abort healthy batches; one
    /// that returns the requested count without having extended anything would hide a split brain.
    /// </para>
    /// </remarks>
    Task<int> ExtendDeliveryAsync(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken);
}
