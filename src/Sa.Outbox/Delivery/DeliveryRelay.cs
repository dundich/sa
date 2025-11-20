using Sa.Classes;
using Sa.Extensions;
using Sa.Outbox.Partitional;
using Sa.Outbox.Publication;
using Sa.Outbox.Repository;
using Sa.Outbox.Support;


namespace Sa.Outbox.Delivery;



/// <summary>
/// Provides functionality to dynamically calculate batch sizes based on current system load or other metrics.
/// </summary>
public interface IBatchSizeCalculator
{
    /// <summary>
    /// Calculates the optimal batch size given a maximum allowed size and a specific tenant ID.
    /// </summary>
    /// <param name="maxAllowedSize">The maximum batch size allowed by settings.</param>
    /// <param name="tenantId">The ID of the tenant for whom the calculation is made.</param>
    /// <returns>The calculated optimal batch size.</returns>
    int CalculateBatchSize(int maxAllowedSize, int tenantId);
}


/// <summary>
/// Handles message relay from persistent storage to delivery handlers with batch processing and error recovery.
/// </summary>
internal sealed class DeliveryRelay(
    IDeliveryRepository repository
    , IArrayPool arrayPool
    , IPartitionalSupportCache partCache
    , TimeProvider timeProvider
    , IDeliveryCourier deliveryCourier
    , PartitionalSettings? partitionalSettings = null
    ) : IDeliveryRelay
{

    private readonly bool _globalForEachTenant = partitionalSettings?.ForEachTenant ?? false;

    public async Task<int> StartDelivery<TMessage>(OutboxDeliverySettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        int batchSize = settings.ExtractSettings.MaxBatchSize;

        if (batchSize == 0) return 0;

        OutboxDeliveryMessage<TMessage>[] buffer = arrayPool.Rent<OutboxDeliveryMessage<TMessage>>(batchSize);
        try
        {
            Memory<OutboxDeliveryMessage<TMessage>> slice = buffer.AsMemory(0, batchSize);

            return _globalForEachTenant || settings.ExtractSettings.ForEachTenant
              ? await ProcessMultipleTenants(slice, settings, cancellationToken)
              : await FillBuffer(slice, settings, 0, cancellationToken);
        }
        finally
        {
            arrayPool.Return(buffer);
        }
    }

    private async Task<int> ProcessMultipleTenants<TMessage>(
        Memory<OutboxDeliveryMessage<TMessage>> slice,
        OutboxDeliverySettings settings,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        int count = 0;
        int[] tenantIds = await partCache.GetTenantIds(cancellationToken);

        foreach (int tenantId in tenantIds)
        {
            count += await FillBuffer(slice, settings, tenantId, cancellationToken);
        }

        return count;
    }

    private async Task<int> FillBuffer<TMessage>(
        Memory<OutboxDeliveryMessage<TMessage>> buffer,
        OutboxDeliverySettings settings,
        int tenantId,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        ExtractSettings extractSettings = settings.ExtractSettings;

        OutboxMessageFilter filter = CreateFilter<TMessage>(timeProvider.GetUtcNow(), extractSettings.LookbackInterval, tenantId);
        var batchSize = buffer.Length;
        var lockDuration = extractSettings.LockDuration;

        // todos calculate batchSize пересчитать по нагрузке


        // Блокируем сообщения для доставки
        int locked = await repository.StartDelivery(buffer,
            batchSize: batchSize,
            lockDuration: lockDuration,
            filter: filter,
            cancellationToken: cancellationToken);


        if (locked == 0) return locked;

        buffer = buffer[..locked];

        // Используем периодическое обновление блокировки
        using IDisposable locker = LockRenewer.KeepLocked(
            extractSettings.LockRenewal
            , async t =>
            {
                filter = filter with { NowDate = timeProvider.GetUtcNow() };
                await repository.ExtendDelivery(lockDuration, filter, t);
            }
            , cancellationToken: cancellationToken
        );

        // send msgs to consumer
        return await DeliverBatches(buffer, settings, filter, cancellationToken);
    }

    private static OutboxMessageFilter CreateFilter<TMessage>(DateTimeOffset now, TimeSpan lookbackInterval, int tenantId)
        where TMessage : IOutboxPayloadMessage
    {
        OutboxMessageTypeInfo ti = OutboxMessageTypeHelper.GetOutboxMessageTypeInfo<TMessage>();

        DateTimeOffset fromDate = now.StartOfDay() - lookbackInterval;

        return new OutboxMessageFilter(
            GenTransactId()
            , typeof(TMessage).Name
            , tenantId
            , ti.PartName
            , fromDate
            , now
        );
    }

    private static string GenTransactId() => $"{Environment.MachineName}-{Guid.NewGuid():N}";

    private async Task<int> DeliverBatches<TMessage>(
        Memory<OutboxDeliveryMessage<TMessage>> deliveryMessages,
        OutboxDeliverySettings settings,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        int successfulDeliveries = 0;

        int batchSize = settings.ConsumeSettings.ConsumeBatchSize ?? settings.ExtractSettings.MaxBatchSize;

        foreach (OutboxContext<TMessage>[] outboxMessages in deliveryMessages
            .GetChunks(batchSize)
            .Select(chunk
                => chunk.Span.SelectWhere(dm
                    => new OutboxContext<TMessage>(dm, timeProvider))))
        {
            if (cancellationToken.IsCancellationRequested) break;

            successfulDeliveries += await DeliveryCourier(settings.ConsumeSettings, filter, outboxMessages, cancellationToken);
        }

        return successfulDeliveries;
    }

    private async Task<int> DeliveryCourier<TMessage>(
        ConsumeSettings settings,
        OutboxMessageFilter filter,
        OutboxContext<TMessage>[] outboxMessages,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        int successfulDeliveries = await deliveryCourier.Deliver(outboxMessages, settings.MaxDeliveryAttempts, cancellationToken);

        await repository.FinishDelivery(outboxMessages, filter, cancellationToken);

        return successfulDeliveries;
    }
}

