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
        Memory<OutboxDeliveryMessage<TMessage>> buffer,
        OutboxDeliverySettings settings,
        int tenantId,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        var extractSettings = settings.ExtractSettings;
        var filter = FilterFactory.CreateFilter<TMessage>(timeProvider.GetUtcNow(), extractSettings.LookbackInterval, tenantId);

        var batchSize = await batcher.CalculateBatchSize(extractSettings.MaxBatchSize, filter, cancellationToken);
        if (batchSize == 0) return 0;

        int locked = await repository.StartDelivery(
            buffer,
            batchSize: batchSize,
            lockDuration: extractSettings.LockDuration,
            filter: filter,
            cancellationToken: cancellationToken);

        if (locked == 0) return 0;

        var messages = buffer[..locked];

        using IDisposable locker = LockRenewer.KeepLocked(
            extractSettings.LockRenewal,
            async t =>
            {
                var renewedFilter = filter with { NowDate = timeProvider.GetUtcNow() };
                await repository.ExtendDelivery(extractSettings.LockDuration, renewedFilter, t);
            },
            cancellationToken: cancellationToken);

        return await DeliverMessages(messages, settings.ConsumeSettings, filter, cancellationToken);
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
                outboxMessages,
                settings.MaxDeliveryAttempts,
                cancellationToken);

            await repository.FinishDelivery(outboxMessages, filter, cancellationToken);
        }

        return successfulDeliveries;
    }
}
