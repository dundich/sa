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


    /// <summary>
    /// Performs bulk insertion of outbox messages, optionally enlisting the writes into a
    /// provider-specific <paramref name="context"/> (for example, an external database transaction).
    /// </summary>
    /// <remarks>
    /// The default implementation forwards to the context-less overload when <paramref name="context"/>
    /// is <c>null</c> and throws <see cref="NotSupportedException"/> otherwise, so a provider that does
    /// not understand contexts fails loudly instead of silently dropping the caller's transaction.
    /// </remarks>
    /// <typeparam name="TMessage">The type of message being saved.</typeparam>
    /// <param name="messages">The collection of outbox messages to save.</param>
    /// <param name="context">An opaque provider-specific publish context, or <c>null</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation, containing the number of messages saved.</returns>
    ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        object? context,
        CancellationToken cancellationToken = default)
            => context is null
                ? InsertBulk(messages, cancellationToken)
                : throw new NotSupportedException(
                    $"Bulk writer '{GetType().Name}' does not support publish contexts.");
}
