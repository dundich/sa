using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Serialization;

namespace Sa.Outbox.InMemory;

/// <summary>
/// The in-memory implementation of <see cref="IOutboxDeliveryManager"/>: lease records from the store,
/// hand them to the pipeline as contexts, apply the batch outcome and renew leases.
/// </summary>
/// <remarks>
/// This is the file to read when embedding a provider: everything a storage has to do is here —
/// select rentable rows, mark them owned with a deadline, and write the consumer's verdict back.
/// Batching, retries, status interpretation and back-off belong to the core.
/// </remarks>
internal sealed class InMemoryOutboxDeliveryManager(
    InMemoryOutboxStore store,
    IOutboxContextFactory contextFactory,
    IOutboxMessageSerializer serializer) : IOutboxDeliveryManager
{
    public Task<int> RentDelivery<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || writeBuffer.IsEmpty)
            return Task.FromResult(0);

        List<InMemoryOutboxRecord> rented = store.Rent(lockDuration, filter, writeBuffer.Length);

        for (int i = 0; i < rented.Count; i++)
        {
            writeBuffer.Span[i] = contextFactory.Create<TMessage>(
                CreateDeliveryMessage<TMessage>(rented[i], filter));
        }

        return Task.FromResult(rented.Count);
    }

    public Task<int> ReturnDelivery<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (messages.IsEmpty)
            return Task.FromResult(0);

        List<InMemoryOutboxOutcome> outcomes = new(messages.Length);

        foreach (IOutboxContextOperations<TMessage> context in messages.Span)
        {
            OutboxRentToken token = context.RentToken;

            if (token.IsNone)
                continue;

            outcomes.Add(new InMemoryOutboxOutcome(
                token.Key,
                token.SubKey,
                context.DeliveryResult,
                context.PostponeDelay));
        }

        return Task.FromResult(store.Return(outcomes, filter));
    }

    public Task<int> ExtendDelivery(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => Task.FromResult(store.Extend(lockExpiration, filter));

    private OutboxDeliveryMessage<TMessage> CreateDeliveryMessage<TMessage>(
        InMemoryOutboxRecord record,
        OutboxMessageFilter filter)
    {
        TMessage payload = OutboxPayloadCodec.Deserialize<TMessage>(serializer, record.Payload)
            ?? throw new InvalidOperationException(
                $"The in-memory Outbox message '{record.OutboxId}' of type '{typeof(TMessage).FullName}' " +
                "deserialized to null.");

        return new OutboxDeliveryMessage<TMessage>(
            OutboxId: record.OutboxId,
            PayloadId: record.PayloadId,
            Payload: payload,
            PartInfo: new OutboxPartInfo(record.TenantId, record.Part, record.CreatedAt),
            DeliveryInfo: new OutboxTaskDeliveryInfo(
                TaskId: record.Id,
                DeliveryId: record.RentGeneration,
                Attempt: record.Attempt,
                LastErrorId: 0,
                Status: default,
                PartInfo: new OutboxPartInfo(record.TenantId, filter.ConsumerGroupId, filter.NowDate)),
            RentToken: new OutboxRentToken(record.Id, record.RentGeneration));
    }
}
