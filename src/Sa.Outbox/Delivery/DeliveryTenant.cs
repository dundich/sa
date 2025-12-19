using System.Buffers;
using Sa.Classes;
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
    public async Task<int> ProcessInTenant<TMessage>(int tenantId, ConsumerGroupSettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {

        ConsumeSettings consumeSettings = settings.ConsumeSettings;


        var filter = FilterFactory.CreateFilter<TMessage>(
            tenantId,
            settings.ConsumerGroupId,
            timeProvider.GetUtcNow(),
            consumeSettings.LookbackInterval,
            consumeSettings.BatchingWindow);

        var batchSize = await batcher.CalculateBatchSize(consumeSettings.MaxBatchSize, filter, cancellationToken);
        batchSize = Math.Min(consumeSettings.MaxBatchSize, batchSize);
        if (batchSize == 0) return 0;

        using var memoryOwner = MemoryPool<IOutboxContextOperations<TMessage>>.Shared.Rent(batchSize);

        var buffer = memoryOwner.Memory[..batchSize];

        var locked = await repository.RentDelivery(
            buffer,
            lockDuration: consumeSettings.LockDuration,
            filter,
            cancellationToken: cancellationToken);

        if (locked == 0) return 0;

        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages = buffer[..locked];

        using IDisposable locker = RenewerLocker(consumeSettings, filter, cancellationToken);


        var successfulDeliveries = await deliveryCourier.Deliver(settings, filter, messages, cancellationToken);

        await repository.ReturnDelivery(
            messages,
            filter with { NowDate = timeProvider.GetUtcNow() },
            cancellationToken);


        return successfulDeliveries;
    }

    private IDisposable RenewerLocker(ConsumeSettings settings, OutboxMessageFilter filter, CancellationToken cancellationToken)
        => LockRenewer.KeepLocked(
                settings.LockRenewal
                , t =>
                {
                    var nowDate = timeProvider.GetUtcNow();
                    return repository.ExtendDelivery(
                        settings.LockDuration
                        , filter with
                        {
                            ToDate = nowDate,
                            NowDate = nowDate
                        }
                        , t);
                }
                , cancellationToken: cancellationToken);
}
