namespace Sa.Outbox.PlugServices;

/// <summary>
/// Represents a repository for storing outbox messages.
/// </summary>
public interface IOutboxBulkWriter
{
    /// <summary>
    /// Inserts a publish batch whose messages share one <see cref="OutboxMessageBatch{TMessage}.PartInfo"/>.
    /// </summary>
    /// <remarks>
    /// This is the shape the core publisher produces: it avoids the per-message
    /// <see cref="OutboxMessage{TMessage}"/> wrapper. The default implementation materializes the
    /// old shape and forwards to <see cref="InsertBulk{TMessage}(ReadOnlyMemory{OutboxMessage{TMessage}}, CancellationToken)"/>,
    /// so a provider written against the old contract keeps working (and keeps allocating one
    /// wrapper per message until it overrides this overload).
    /// </remarks>
    ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken = default)
            => InsertBulk(batch, context: null, cancellationToken);

    /// <summary>
    /// Inserts a publish batch, optionally enlisting the writes into a provider-specific
    /// <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// The default implementation materializes <see cref="OutboxMessage{TMessage}"/> for every
    /// item and forwards to the context-aware old overload, so old providers remain correct.
    /// </remarks>
    ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default)
    {
        if (batch.IsEmpty)
        {
            return new ValueTask<ulong>(0);
        }

        ReadOnlySpan<OutboxMessageBatchItem<TMessage>> items = batch.Messages.Span;
        OutboxMessage<TMessage>[] messages = new OutboxMessage<TMessage>[items.Length];

        for (int i = 0; i < items.Length; i++)
        {
            OutboxMessageBatchItem<TMessage> item = items[i];
            messages[i] = new OutboxMessage<TMessage>(item.PayloadId, item.Payload, batch.PartInfo);
        }

        return InsertBulk<TMessage>(messages, context, cancellationToken);
    }

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
