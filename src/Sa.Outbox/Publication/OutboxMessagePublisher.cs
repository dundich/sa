using Sa.Classes;
using Sa.Outbox.Support;
using Sa.Timing.Providers;

namespace Sa.Outbox.Publication;

internal class OutboxMessagePublisher(
    ICurrentTimeProvider timeProvider,
    IArrayPoolFactory poolFactory,
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
        IArrayPooler<OutboxMessage<TMessage>> pooler = poolFactory.Create<OutboxMessage<TMessage>>();
        IEnumerator<TMessage> enumerator = messages.GetEnumerator();

        ulong sent = 0;
        int start = 0;
        do
        {
            int len = (start + maxBatchSize < messages.Count)
                ? maxBatchSize
                : messages.Count - start;

            OutboxMessage<TMessage>[] payloads = pooler.Rent(len);
            try
            {
                int i = 0;
                while (i < len && enumerator.MoveNext())
                {
                    TMessage message = enumerator.Current;

                    payloads[i] = new OutboxMessage<TMessage>(
                        PayloadId: message.Message ?? string.Empty,
                        Payload: message,
                        PartInfo: new OutboxPartInfo(TenantId: message.TenantId, typeInfo.PartName, now));

                    i++;
                }

                sent += await outboxRepository.Save<TMessage>(payloads.AsMemory()[..len], cancellationToken);
            }
            finally
            {
                pooler.Return(payloads);
            }

            start += len;
        }
        while (start < messages.Count);

        return sent;

    }
}
