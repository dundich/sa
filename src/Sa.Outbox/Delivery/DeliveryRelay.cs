using Sa.Classes;
using Sa.Outbox.Partitional;
using Sa.Outbox.Support;


namespace Sa.Outbox.Delivery;


/// <summary>
/// Handles message relay from persistent storage to delivery handlers with batch processing and error recovery.
/// </summary>
internal sealed class DeliveryRelay(
    ITenantMessageProcessor messageProcessor,
    IPartitionalSupportCache partCache,
    IArrayPool arrayPool,
    PartitionalSettings? partitionalSettings = null
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
              : await messageProcessor.ProcessTenantMessages(slice, settings, 0, cancellationToken);
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
            count += await messageProcessor.ProcessTenantMessages(slice, settings, tenantId, cancellationToken);
        }

        return count;
    }
}
