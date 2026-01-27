using Sa.Classes;
using Sa.Outbox.Metadata;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Publication;

internal sealed class OutboxMessagePublisher(
    TimeProvider timeProvider,
    IOutboxBulkWriter bulkWriter,
    OutboxPublishSettings publishSettings,
    IOutboxMessageMetadataProvider metadataProvider
) : IOutboxMessagePublisher
{
    public async ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        int tenantId = 0,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) return 0;
        return await Send(messages, tenantId, cancellationToken);
    }

    private async ValueTask<ulong> Send<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        CancellationToken cancellationToken)
    {
        var typeInfo = metadataProvider.GetMetadata<TMessage>();
        DateTimeOffset now = timeProvider.GetUtcNow();
        int maxBatchSize = publishSettings.MaxBatchSize;

        using IEnumerator<TMessage> enumerator = messages.GetEnumerator();

        ulong sent = 0;
        int start = 0;
        do
        {
            int len = (start + maxBatchSize < messages.Count)
                ? maxBatchSize
                : messages.Count - start;

            OutboxMessage<TMessage>[] payloads = DefaultArrayPool.Shared.Rent<OutboxMessage<TMessage>>(len);
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

                sent += await bulkWriter.InsertBulk<TMessage>(payloads.AsMemory()[..len], cancellationToken);
            }
            finally
            {
                DefaultArrayPool.Shared.Return(payloads);
            }

            start += len;
        }
        while (start < messages.Count);

        return sent;
    }
}
