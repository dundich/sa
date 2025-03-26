namespace Sa.Outbox;

/// <summary>
/// Represents a repository for storing outbox messages.
/// </summary>
public interface IOutboxRepository
{
    /// <summary>
    /// Saves a collection of outbox messages to the repository.
    /// </summary>
    /// <typeparam name="TMessage">The type of message being saved.</typeparam>
    /// <param name="payloadType">The type of payload being saved.</param>
    /// <param name="messages">The collection of outbox messages to save.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation, containing the number of messages saved.</returns>
    ValueTask<ulong> Save<TMessage>(ReadOnlyMemory<OutboxMessage<TMessage>> messages, CancellationToken cancellationToken = default);
}