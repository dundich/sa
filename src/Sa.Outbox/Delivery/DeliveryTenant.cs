using System.Buffers;
using Sa.Classes;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Support;


namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal sealed class DeliveryTenant(
    IOutboxDeliveryManager deliveryMan,
    TimeProvider timeProvider,
    IDeliveryCourier deliveryCourier,
    IDeliveryBatcher batcher) : IDeliveryTenant
{
    public async Task<int> ProcessInTenant<TMessage>(
        int tenantId,
        ConsumerGroupSettings settings,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        var filter = CreateFilter<TMessage>(tenantId, settings);

        var batchSize = await CalculateBatchSizeAsync(settings.ConsumeSettings, filter, cancellationToken);
        if (batchSize == 0) return 0;

        using var memoryOwner = RentMemory<TMessage>(batchSize);


        var messages = await AcquireMessagesAsync(
            settings.ConsumeSettings,
            filter,
            memoryOwner.Memory[..batchSize],
            cancellationToken);

        if (messages.IsEmpty) return 0;

        using IDisposable locker = RenewerLocker(settings.ConsumeSettings, filter, cancellationToken);

        var successfulDeliveries = await deliveryCourier.Deliver(settings, filter, messages, cancellationToken);

        await ReleaseMessagesAsync(messages, filter, cancellationToken);

        return successfulDeliveries;
    }

    private OutboxMessageFilter CreateFilter<TMessage>(int tenantId, ConsumerGroupSettings settings)
        where TMessage : IOutboxPayloadMessage
    {
        return FilterFactory.CreateFilter<TMessage>(
            tenantId,
            settings.ConsumerGroupId,
            timeProvider.GetUtcNow(),
            settings.ConsumeSettings.LookbackInterval,
            settings.ConsumeSettings.BatchingWindow);
    }

    private static IMemoryOwner<IOutboxContextOperations<TMessage>> RentMemory<TMessage>(int size)
        where TMessage : IOutboxPayloadMessage
    {
        return MemoryPool<IOutboxContextOperations<TMessage>>.Shared.Rent(size);
    }

    private async Task<int> CalculateBatchSizeAsync(
        ConsumeSettings consumeSettings,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        var calculatedSize = await batcher.CalculateBatchSize(
            consumeSettings.MaxBatchSize,
            filter,
            cancellationToken);

        return Math.Min(consumeSettings.MaxBatchSize, calculatedSize);
    }

    private async Task<ReadOnlyMemory<IOutboxContextOperations<TMessage>>> AcquireMessagesAsync<TMessage>(
        ConsumeSettings consumeSettings,
        OutboxMessageFilter filter,
        Memory<IOutboxContextOperations<TMessage>> buffer,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        var lockedCount = await deliveryMan.RentDelivery(
            buffer,
            consumeSettings.LockDuration,
            filter,
            cancellationToken);

        return buffer[..lockedCount];
    }


    private Task<int> ReleaseMessagesAsync<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        return deliveryMan.ReturnDelivery(
            messages,
            filter with { NowDate = timeProvider.GetUtcNow() },
            cancellationToken);
    }

    private IDisposable RenewerLocker(
        ConsumeSettings settings,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => LockRenewer.KeepLocked(
                settings.LockRenewal
                , t =>
                {
                    var nowDate = timeProvider.GetUtcNow();
                    return deliveryMan.ExtendDelivery(
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
