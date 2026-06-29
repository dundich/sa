using Sa.Classes;
using Sa.Outbox.PlugServices;
using System.Buffers;


namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal sealed class DeliveryTenant(
    IOutboxDeliveryManager deliveryMan,
    IDeliveryCourier deliveryCourier,
    IDeliveryBatcher batcher,
    FilterFactory filterFactory,
    TimeProvider? timeProvider = null) : IDeliveryTenant
{

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<int> ProcessInTenant<TMessage>(
        int tenantId,
        OutboxConsumerSettings settings,
        CancellationToken cancellationToken)
    {
        var filter = CreateFilter<TMessage>(tenantId, settings);

        var batchSize = await CalculateBatchSizeAsync(settings, filter, cancellationToken);
        if (batchSize == 0) return 0;

        using var memoryOwner = RentMemory<TMessage>(batchSize);


        var messages = await AcquireMessagesAsync(
            settings,
            filter,
            memoryOwner.Memory[..batchSize],
            cancellationToken);

        if (messages.IsEmpty) return 0;

        await using IAsyncDisposable locker = RenewerLocker(settings, filter, cancellationToken);

        var successfulDeliveries = await deliveryCourier.Deliver(settings, filter, messages, cancellationToken);

        await ReleaseMessagesAsync(messages, filter, cancellationToken);

        return successfulDeliveries;
    }

    private OutboxMessageFilter CreateFilter<TMessage>(int tenantId, OutboxConsumerSettings settings)
    {
        return filterFactory.CreateFilter<TMessage>(
            tenantId: tenantId,
            consumerGroupId: settings.ConsumerGroupId,
            now: GetUtcNow(),
            lookbackInterval: settings.LookbackInterval,
            batchingWindow: settings.BatchingWindow);
    }

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    private async Task<int> CalculateBatchSizeAsync(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        var calculatedSize = await batcher.CalculateBatchSize(
            settings.MaxBatchSize,
            filter,
            cancellationToken);

        return Math.Clamp(calculatedSize, 0, settings.MaxBatchSize);
    }

    private async Task<ReadOnlyMemory<IOutboxContextOperations<TMessage>>> AcquireMessagesAsync<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        Memory<IOutboxContextOperations<TMessage>> buffer,
        CancellationToken cancellationToken)
    {
        var lockedCount = await deliveryMan.RentDelivery(
            buffer,
            settings.LockDuration,
            filter,
            cancellationToken);

        return buffer[..lockedCount];
    }


    private Task<int> ReleaseMessagesAsync<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        return deliveryMan.ReturnDelivery(
            messages,
            filter with { NowDate = GetUtcNow() },
            cancellationToken);
    }

    private IAsyncDisposable RenewerLocker(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => LockRenewer.KeepLocked(
                settings.LockRenewal
                , t =>
                {
                    var nowDate = GetUtcNow();
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

    private static IMemoryOwner<IOutboxContextOperations<TMessage>> RentMemory<TMessage>(int size)
        => MemoryPool<IOutboxContextOperations<TMessage>>.Shared.Rent(size);
}
