using Sa.Classes;
using Sa.Outbox.Support;

namespace Sa.Outbox.Publication;

internal sealed class OutboxMessagePublisher(
    TimeProvider timeProvider,
    IOutboxRepository outboxRepository,
    OutboxPublishSettings publishSettings
) : IOutboxMessagePublisher
{
    public async ValueTask<ulong> Publish<TMessage>(IReadOnlyCollection<TMessage> messages, CancellationToken cancellationToken = default)
        where TMessage : IOutboxPayloadMessage
    {
        if (messages.Count == 0) return 0;
        return await Send(messages, cancellationToken);
    }

    private async ValueTask<ulong> Send<TMessage>(IReadOnlyCollection<TMessage> messages, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        OutboxMessageTypeInfo typeInfo = OutboxMessageTypeHelper.GetOutboxMessageTypeInfo<TMessage>();
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

                    payloadsSpan[count] = new OutboxMessage<TMessage>(
                        PayloadId: message.PayloadId ?? string.Empty,
                        Payload: message,
                        PartInfo: new OutboxPartInfo(TenantId: message.TenantId, typeInfo.PartName, now));

                    count++;
                }

                sent += await outboxRepository.Save<TMessage>(payloads.AsMemory()[..len], cancellationToken);
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
