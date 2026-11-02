using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis.Commands;

namespace Sa.Outbox.Redis.Services.Plug;

/// <summary>
/// The Redis implementation of <see cref="IOutboxBulkWriter"/>: appends the batch to its stream.
/// </summary>
/// <remarks>
/// A publish context is not supported. The Redis counterpart of "enlist in the caller's
/// transaction" would be a caller-owned <c>ITransaction</c>, and this provider deliberately does
/// not implement one. Rather than accept a context and silently ignore it — the one thing a caller
/// passing a transaction must never see — the writer refuses it.
/// </remarks>
internal sealed class OutboxBulkWriter(PublishMessagesCommand publishCommand) : IOutboxBulkWriter
{
    public ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken = default)
            => InsertBulk(batch, context: null, cancellationToken);

    public ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default)
    {
        EnsureNoContext(context);

        return batch.IsEmpty
            ? ValueTask.FromResult(0UL)
            : publishCommand.ExecuteAsync(batch, cancellationToken);
    }

    public ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken = default)
            => InsertBulk(messages, context: null, cancellationToken);

    public async ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        object? context,
        CancellationToken cancellationToken = default)
    {
        EnsureNoContext(context);

        if (messages.Length == 0) return 0;

        // The old (per-message) shape carries a part on every message, so a call may span several
        // parts. Group them back into per-part batches, the shape one stream expects.
        Dictionary<OutboxPartInfo, List<OutboxMessageBatchItem<TMessage>>> groups = new();

        foreach (OutboxMessage<TMessage> message in messages.Span)
        {
            if (!groups.TryGetValue(message.PartInfo, out List<OutboxMessageBatchItem<TMessage>>? items))
            {
                items = [];
                groups[message.PartInfo] = items;
            }

            items.Add(new OutboxMessageBatchItem<TMessage>(message.PayloadId, message.Payload));
        }

        ulong inserted = 0;

        foreach ((OutboxPartInfo partInfo, List<OutboxMessageBatchItem<TMessage>> items) in groups)
        {
            var batch = new OutboxMessageBatch<TMessage>(items.ToArray(), partInfo);
            inserted += await publishCommand.ExecuteAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        return inserted;
    }

    private static void EnsureNoContext(object? context)
    {
        if (context is not null)
        {
            throw new NotSupportedException(
                $"The Redis Outbox bulk writer does not support publish contexts (got '{context.GetType().FullName}'). " +
                "The Redis provider does not implement an enlistable transaction.");
        }
    }
}
