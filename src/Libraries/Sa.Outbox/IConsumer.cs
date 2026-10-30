using Sa.Outbox.Delivery;

namespace Sa.Outbox;

/// <summary>
/// Represents a consumer interface for processing Outbox messages of a specific type.
/// </summary>
/// <typeparam name="TMessage">The type of the message being consumed.</typeparam>
public interface IConsumer<TMessage> : IConsumer
{
    /// <summary>
    /// Consumes a collection of Outbox messages.
    /// This method processes the provided messages asynchronously.
    /// </summary>
    /// <param name="messages">A read-only collection of Outbox contexts containing messages to be consumed.</param>
    /// <param name="cancellationToken">A cancellation token to signal the operation's cancellation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// The supplied memory and every context in it are owned by the delivery pipeline for the
    /// duration of this call. Once the returned task completes the statuses are persisted and the
    /// contexts are recycled, so a consumer must not retain the memory, a context, or the payload
    /// through them beyond this call.
    /// </remarks>
    ValueTask Consume(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken);
}

/// <summary>
/// Represents a base consumer interface for processing Outbox messages.
/// This interface can be extended by specific consumer implementations.
/// </summary>
public interface IConsumer
{
}
