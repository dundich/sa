using Sa.Outbox.Metadata;
using Sa.Outbox.PlugServices;
using System.Buffers;

namespace Sa.Outbox.Publication;

internal sealed class OutboxMessagePublisher(
    TimeProvider timeProvider,
    IOutboxBulkWriter bulkWriter,
    OutboxPublishSettings publishSettings,
    IOutboxMessageMetadataProvider metadataProvider) : IOutboxMessagePublisher
{
    public async ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        object? context,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) return 0;

        return await Send(messages, tenantId, context, cancellationToken);
    }

    public async ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        int tenantId,
        object? context,
        CancellationToken cancellationToken = default)
    {
        OutboxMessageMetadata typeInfo = metadataProvider.GetMetadata<TMessage>();
        var partInfo = new OutboxPartInfo(tenantId, typeInfo.PartName, timeProvider.GetUtcNow());

        // One pooled slot instead of the [message] collection the default interface method
        // allocated and re-enumerated through Send: a single publish is a common call, and the
        // array was pure overhead in front of a bulk writer that takes memory anyway.
        OutboxMessageBatchItem<TMessage>[] buffer =
            ArrayPool<OutboxMessageBatchItem<TMessage>>.Shared.Rent(1);

        try
        {
            buffer[0] = CreateItem(message, typeInfo);

            return await bulkWriter.InsertBulk<TMessage>(
                new OutboxMessageBatch<TMessage>(buffer.AsMemory(0, 1), partInfo),
                context,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Same reason as in Send: the rented slot must not keep the payload rooted.
            ArrayPool<OutboxMessageBatchItem<TMessage>>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async ValueTask<ulong> Send<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        object? context,
        CancellationToken cancellationToken)
    {
        OutboxMessageMetadata typeInfo = metadataProvider.GetMetadata<TMessage>();

        DateTimeOffset now = timeProvider.GetUtcNow();
        int maxBatchSize = publishSettings.MaxBatchSize;

        // Every message of one publish call has the same tenant, part and timestamp, so one
        // immutable OutboxPartInfo can be shared by the whole publish instead of allocating a
        // fresh record per message. The providers only read its fields.
        var partInfo = new OutboxPartInfo(TenantId: tenantId, typeInfo.PartName, now);

        using IEnumerator<TMessage> enumerator = messages.GetEnumerator();

        ulong sent = 0;
        int start = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            int len = (start + maxBatchSize < messages.Count)
                ? maxBatchSize
                : messages.Count - start;

            OutboxMessageBatchItem<TMessage>[] payloads =
                ArrayPool<OutboxMessageBatchItem<TMessage>>.Shared.Rent(len);
            Span<OutboxMessageBatchItem<TMessage>> payloadsSpan = payloads;

            try
            {
                int count = 0;
                while (count < len && enumerator.MoveNext())
                {
                    TMessage message = enumerator.Current;

                    payloadsSpan[count] = CreateItem(message, typeInfo);

                    count++;
                }

                // Count can lie. Sending `len` rather than the number of items actually written
                // copied default structs (and, through the pool, the previous batch's payloads)
                // into the bulk writer.
                if (count == 0)
                    break;

                sent += await bulkWriter.InsertBulk<TMessage>(
                    new OutboxMessageBatch<TMessage>(payloads.AsMemory(0, count), partInfo),
                    context,
                    cancellationToken);

                start += count;
                if (count < len)
                    break;
            }
            finally
            {
                // clearArray: the rented buffer is larger than `len` and holds payload references.
                // Without this, they stay rooted in the pool until those slots are overwritten.
                ArrayPool<OutboxMessageBatchItem<TMessage>>.Shared.Return(payloads, clearArray: true);
            }
        }
        while (start < messages.Count);

        return sent;
    }

    private static OutboxMessageBatchItem<TMessage> CreateItem<TMessage>(
        TMessage message,
        OutboxMessageMetadata typeInfo)
        => new(
            PayloadId: typeInfo.GetPayloadId(message) ?? string.Empty,
            Payload: message);
}
