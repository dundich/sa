namespace Sa.Utils.WorkQueue;

/// <summary>
/// Whether an accepted item keeps observing the cancellation token its producer passed
/// to <see cref="ISaWorkQueue{TInput}.Enqueue"/>.
/// </summary>
public enum SaProducerCancellationMode
{
    /// <summary>
    /// The token is stored with the item. Cancelling it while the item is buffered or
    /// in flight reports <see cref="SaWorkStatus.Aborted"/> and leaves the reader in the
    /// pool. This is the default and the only mode in which <see cref="SaWorkStatus.Aborted"/>
    /// can come from the producer.
    /// </summary>
    /// <remarks>
    /// A cancellable token costs a linked <see cref="CancellationTokenSource"/> per item,
    /// because the reader has to combine it with its own token. That is the price of
    /// being able to abandon an accepted item.
    /// </remarks>
    Observe = 0,

    /// <summary>
    /// The token is not stored with the item. It still cancels a producer parked in
    /// <c>WriteAsync</c> because the buffer is full, and a token already cancelled when
    /// the item is offered rejects that item — but once accepted, the item runs to a
    /// terminal status whatever happens to the producer's token.
    /// </summary>
    /// <remarks>
    /// This is the mode for work that must outlive the scope that produced it — a request
    /// token handed to background processing, for example. It removes the per-item linked
    /// source entirely, which is the dominant allocation on the enqueue path when callers
    /// pass a live token.
    /// </remarks>
    Detached = 1,
}
