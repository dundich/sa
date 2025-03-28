using Sa.Classes;
using Sa.Extensions;
using Sa.Outbox.Partitional;
using Sa.Outbox.Publication;
using Sa.Outbox.Repository;
using Sa.Timing.Providers;

namespace Sa.Outbox.Delivery;

internal sealed class DeliveryRelay(
    IDeliveryRepository repository
    , IArrayPoolFactory arrayPoolFactory
    , IPartitionalSupportCache partCache
    , ICurrentTimeProvider timeProvider
    , IDeliveryCourier deliveryCourier
    , PartitionalSettings? partitionalSettings = null
    ) : IDeliveryRelay
{

    private readonly bool _globalForEachTenant = partitionalSettings?.ForEachTenant ?? false;

    public async Task<int> StartDelivery<TMessage>(OutboxDeliverySettings settings, CancellationToken cancellationToken)
    {
        IArrayPooler<OutboxDeliveryMessage<TMessage>> arrayPooler = arrayPoolFactory.Create<OutboxDeliveryMessage<TMessage>>();
        int batchSize = settings.ExtractSettings.MaxBatchSize;

        if (batchSize == 0) return 0;

        OutboxDeliveryMessage<TMessage>[] buffer = arrayPooler.Rent(batchSize);
        Memory<OutboxDeliveryMessage<TMessage>> slice = buffer.AsMemory(0, batchSize);
        try
        {
            return _globalForEachTenant || settings.ExtractSettings.ForEachTenant
              ? await ProcessMultipleTenants(slice, settings, cancellationToken)
              : await FillBuffer(slice, settings, 0, cancellationToken);
        }
        finally
        {
            arrayPooler.Return(buffer);
        }
    }

    private async Task<int> ProcessMultipleTenants<TMessage>(Memory<OutboxDeliveryMessage<TMessage>> slice, OutboxDeliverySettings settings, CancellationToken cancellationToken)
    {
        int count = 0;
        int[] tenantIds = await partCache.GetTenantIds(cancellationToken);

        foreach (int tenantId in tenantIds)
        {
            count += await FillBuffer(slice, settings, tenantId, cancellationToken);
        }

        return count;
    }

    private async Task<int> FillBuffer<TMessage>(Memory<OutboxDeliveryMessage<TMessage>> buffer, OutboxDeliverySettings settings, int tenantId, CancellationToken cancellationToken)
    {
        OutboxMessageFilter filter = CreateFilter<TMessage>(settings, tenantId);

        int locked = await repository.StartDelivery(buffer, settings.ExtractSettings.MaxBatchSize, settings.ExtractSettings.LockDuration, filter, cancellationToken);
        if (locked == 0) return locked;

        buffer = buffer[..locked];

        using IDisposable locker = KeepLocker.KeepLocked(
            settings.ExtractSettings.LockRenewal
            , async t =>
            {
                filter = ExtendFilter(filter);
                await repository.ExtendDelivery(settings.ExtractSettings.LockDuration, filter, t);
            }
            , cancellationToken: cancellationToken
        );

        // send msgs to consumer
        return await DeliverBatches(buffer, settings, filter, cancellationToken);
    }

    private OutboxMessageFilter CreateFilter<TMessage>(OutboxDeliverySettings settings, int tenantId)
    {
        OutboxMessageTypeInfo ti = OutboxMessageTypeHelper.GetOutboxMessageTypeInfo<TMessage>();
        DateTimeOffset fromDate = timeProvider.GetUtcNow().StartOfDay() - settings.ExtractSettings.LookbackInterval;

        return new OutboxMessageFilter(
            GenTransactId()
            , typeof(TMessage).Name
            , tenantId
            , ti.PartName
            , fromDate
            , timeProvider.GetUtcNow()
        );
    }

    private OutboxMessageFilter ExtendFilter(OutboxMessageFilter filter)
    {
        return new OutboxMessageFilter(
            filter.TransactId
            , filter.PayloadType
            , filter.TenantId
            , filter.Part
            , filter.FromDate
            , timeProvider.GetUtcNow()
        );
    }

    private static string GenTransactId() => $"{Environment.MachineName}-{Guid.NewGuid():N}";

    private async Task<int> DeliverBatches<TMessage>(Memory<OutboxDeliveryMessage<TMessage>> deliveryMessages, OutboxDeliverySettings settings, OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        int successfulDeliveries = 0;

        foreach (IOutboxContext<TMessage>[] outboxMessages in deliveryMessages
            .GetChunks(settings.ConsumeSettings.ConsumeBatchSize ?? settings.ExtractSettings.MaxBatchSize)
            .Select(chunk
                => chunk.Span.SelectWhere(dm
                    => new OutboxContext<TMessage>(dm, timeProvider))))
        {
            if (cancellationToken.IsCancellationRequested) break;

            successfulDeliveries += await DeliveryCourier(settings, filter, outboxMessages, cancellationToken);
        }

        return successfulDeliveries;
    }

    private async Task<int> DeliveryCourier<TMessage>(OutboxDeliverySettings settings, OutboxMessageFilter filter, IOutboxContext<TMessage>[] outboxMessages, CancellationToken cancellationToken)
    {
        int successfulDeliveries = await deliveryCourier.Deliver(outboxMessages, settings.ConsumeSettings.MaxDeliveryAttempts, cancellationToken);

        await repository.FinishDelivery(outboxMessages, filter, cancellationToken);

        return successfulDeliveries;
    }
}
