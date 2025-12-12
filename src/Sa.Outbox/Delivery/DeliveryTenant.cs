using Sa.Classes;
using Sa.Extensions;
using Sa.Outbox.Repository;
using Sa.Outbox.Support;


namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal sealed class DeliveryTenant(
    IDeliveryRepository repository,
    TimeProvider timeProvider,
    IDeliveryCourier deliveryCourier,
    IDeliveryBatcher batcher) : IDeliveryTenant
{
    public async Task<int> Process<TMessage>(
        int tenantId,
        ConsumeSettings settings,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {

        var filter = FilterFactory.CreateFilter<TMessage>(
            settings.ConsumerGroupId,
            timeProvider.GetUtcNow(),
            settings.LookbackInterval,
            tenantId);

        var batchSize = await batcher.CalculateBatchSize(settings.MaxBatchSize, filter, cancellationToken);
        if (batchSize == 0) return 0;


        OutboxDeliveryMessage<TMessage>[] buffer = DefaultArrayPool.Shared.Rent<OutboxDeliveryMessage<TMessage>>(batchSize);
        try
        {
            Memory<OutboxDeliveryMessage<TMessage>> slice = buffer.AsMemory(0, batchSize);

            int locked = await repository.StartDelivery(
                slice,
                batchSize: Math.Min(settings.MaxBatchSize, batchSize),
                lockDuration: settings.LockDuration,
                filter: filter,
                cancellationToken: cancellationToken);

            if (locked == 0) return 0;

            Memory<OutboxDeliveryMessage<TMessage>> messages = slice[..locked];

            using IDisposable locker = RenewerLocker(settings, filter, cancellationToken);

            return await DeliverMessages(messages, settings, filter, cancellationToken);
        }
        finally
        {
            DefaultArrayPool.Shared.Return(buffer);
        }
    }


    private IDisposable RenewerLocker(ConsumeSettings settings, OutboxMessageFilter filter, CancellationToken cancellationToken)
        => LockRenewer.KeepLocked(
            settings.LockRenewal,
            t => repository.ExtendDelivery(settings.LockDuration, filter with { ToDate = timeProvider.GetUtcNow() }, t),
            cancellationToken: cancellationToken);


    private async Task<int> DeliverMessages<TMessage>(
        Memory<OutboxDeliveryMessage<TMessage>> deliveryMessages,
        ConsumeSettings settings,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        int successfulDeliveries = 0;
        int batchSize = settings.ConsumeBatchSize ?? deliveryMessages.Length;

        foreach (OutboxContext<TMessage>[] outboxMessages in deliveryMessages
            .GetChunks(batchSize)
            .Select(chunk => chunk.Span.SelectWhere(dm => new OutboxContext<TMessage>(dm, timeProvider))))
        {
            if (cancellationToken.IsCancellationRequested) break;

            successfulDeliveries += await deliveryCourier.Deliver(
                settings,
                outboxMessages,
                cancellationToken);

            await repository.FinishDelivery(outboxMessages, filter, cancellationToken);
        }

        return successfulDeliveries;
    }
}
