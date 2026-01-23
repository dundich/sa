namespace Sa.Outbox.PlugServices;

/// <summary>
/// Represents a repository for storing outbox messages.
/// </summary>
public interface IOutboxBulkWriter
{
    /// <summary>
    /// Performs bulk insertion of outbox messages
    /// Optimized for high-throughput scenarios with multiple messages.
    /// </summary>
    /// <typeparam name="TMessage">The type of message being saved.</typeparam>
    /// <param name="messages">The collection of outbox messages to save.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation, containing the number of messages saved.</returns>
    ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken = default);
}
