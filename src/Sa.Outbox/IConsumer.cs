using Sa.Outbox.Support;

namespace Sa.Outbox;

/// <summary>
/// Represents a consumer interface for processing Outbox messages of a specific type.
/// </summary>
/// <typeparam name="TMessage">The type of the message being consumed.</typeparam>
public interface IConsumer<TMessage> : IConsumer
    where TMessage : IOutboxPayloadMessage
{
    /// <summary>
    /// Consumes a collection of Outbox messages.
    /// This method processes the provided messages asynchronously.
    /// </summary>
    /// <param name="outboxMessages">A read-only collection of Outbox contexts containing messages to be consumed.</param>
    /// <param name="cancellationToken">A cancellation token to signal the operation's cancellation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    ValueTask Consume(
        ConsumeSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> outboxMessages,
        CancellationToken cancellationToken);
}

/// <summary>
/// Represents a base consumer interface for processing Outbox messages.
/// This interface can be extended by specific consumer implementations.
/// </summary>
public interface IConsumer
{
}
