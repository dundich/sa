namespace Sa.Outbox.PlugServices;


/// <summary>
/// needed External implementation
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
    Task<int> ExtendDelivery(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken);
}
