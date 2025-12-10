using Sa.Classes;
using Sa.Extensions;
using Sa.Outbox.Repository;
using Sa.Outbox.Support;


namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal sealed class TenantMessageProcessor(
    IDeliveryRepository repository,
    TimeProvider timeProvider,
    IDeliveryCourier deliveryCourier,
    IDeliveryBatcher batcher) : ITenantMessageProcessor
{
    public async Task<int> ProcessTenantMessages<TMessage>(
        ConsumeSettings settings,
        Memory<OutboxDeliveryMessage<TMessage>> buffer,
        int tenantId,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {

        var filter = FilterFactory.CreateFilter<TMessage>(
            settings.ConsumerGroupId,
            timeProvider.GetUtcNow(),
            settings.LookbackInterval,
            tenantId);

        var batchSize = await batcher.CalculateBatchSize(settings.MaxBatchSize, filter, cancellationToken);
        if (batchSize == 0) return 0;

        int locked = await repository.StartDelivery(
            buffer,
            batchSize: Math.Min(settings.MaxBatchSize, batchSize),
            lockDuration: settings.LockDuration,
            filter: filter,
            cancellationToken: cancellationToken);

        if (locked == 0) return 0;

        var messages = buffer[..locked];

        using IDisposable locker = LockRenewer.KeepLocked(
            settings.LockRenewal,
            async t =>
            {
                var renewedFilter = filter with { ToDate = timeProvider.GetUtcNow() };
                await repository.ExtendDelivery(settings.LockDuration, renewedFilter, t);
            },
            cancellationToken: cancellationToken);

        return await DeliverMessages(messages, settings, filter, cancellationToken);
    }

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
