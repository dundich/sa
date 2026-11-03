using Sa.Outbox.PlugServices;
using Sa.Outbox.Serialization;

namespace Sa.Outbox.InMemory;

/// <summary>
/// The in-memory implementation of <see cref="IOutboxBulkWriter"/>: serializes the batch and appends
/// it to the store.
/// </summary>
/// <remarks>
/// A publish context is not supported. There is no transaction to enlist into — the store is a
/// process-local dictionary — so a caller passing one gets an exception instead of a silently ignored
/// transactional guarantee.
/// </remarks>
internal sealed class InMemoryOutboxBulkWriter(
    InMemoryOutboxStore store,
    IOutboxMessageSerializer serializer) : IOutboxBulkWriter
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

        if (batch.IsEmpty)
            return ValueTask.FromResult(0UL);

        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlySpan<OutboxMessageBatchItem<TMessage>> messages = batch.Messages.Span;
        List<InMemoryOutboxRecord> records = new(messages.Length);

        foreach (OutboxMessageBatchItem<TMessage> item in messages)
        {
            records.Add(new InMemoryOutboxRecord
            {
                TenantId = batch.PartInfo.TenantId,
                Part = batch.PartInfo.Part,
                PayloadType = typeof(TMessage).Name,
                PayloadId = item.PayloadId,
                Payload = OutboxPayloadCodec.Serialize(serializer, item.Payload),
                CreatedAt = batch.PartInfo.CreatedAt,
            });
        }

        return ValueTask.FromResult(store.Insert(records));
    }

    private static void EnsureNoContext(object? context)
    {
        if (context is not null)
        {
            throw new NotSupportedException(
                $"The in-memory Outbox bulk writer does not support publish contexts (got '{context.GetType().FullName}'). " +
                "There is no transaction to enlist into; the store lives in process memory.");
        }
    }
}
