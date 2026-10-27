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

    private async ValueTask<ulong> Send<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        object? context,
        CancellationToken cancellationToken)
    {
        OutboxMessageMetadata typeInfo = metadataProvider.GetMetadata<TMessage>();

        DateTimeOffset now = timeProvider.GetUtcNow();
        int maxBatchSize = publishSettings.MaxBatchSize;

        using IEnumerator<TMessage> enumerator = messages.GetEnumerator();

        ulong sent = 0;
        int start = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            int len = (start + maxBatchSize < messages.Count)
                ? maxBatchSize
                : messages.Count - start;

            OutboxMessage<TMessage>[] payloads = ArrayPool<OutboxMessage<TMessage>>.Shared.Rent(len);
            Span<OutboxMessage<TMessage>> payloadsSpan = payloads;

            try
            {
                int count = 0;
                while (count < len && enumerator.MoveNext())
                {
                    TMessage message = enumerator.Current;

                    var payloadId = typeInfo.GetPayloadId(message!) ?? string.Empty;

                    payloadsSpan[count] = new OutboxMessage<TMessage>(
                        PayloadId: payloadId,
                        Payload: message,
                        PartInfo: new OutboxPartInfo(TenantId: tenantId, typeInfo.PartName, now));

                    count++;
                }

                // Count can lie. Sending `len` rather than the number of items actually written
                // copied default structs (and, through the pool, the previous batch's payloads)
                // into the bulk writer.
                if (count == 0)
                    break;

                sent += await bulkWriter.InsertBulk<TMessage>(
                    payloads.AsMemory()[..count], context, cancellationToken);

                start += count;
                if (count < len)
                    break;
            }
            finally
            {
                // clearArray: the rented buffer is larger than `len` and holds TMessage. Without
                // this, payloads stay rooted in the pool until those slots happen to be overwritten.
                ArrayPool<OutboxMessage<TMessage>>.Shared.Return(payloads, clearArray: true);
            }
        }
        while (start < messages.Count);

        return sent;
    }
}
