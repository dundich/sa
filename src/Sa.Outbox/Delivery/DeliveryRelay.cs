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
    IArrayPool arrayPool
    ) : IDeliveryRelay
{
    public async Task<int> StartDelivery<TMessage>(ConsumeSettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        int batchSize = settings.MaxBatchSize;

        if (batchSize == 0) return 0;

        OutboxDeliveryMessage<TMessage>[] buffer = arrayPool.Rent<OutboxDeliveryMessage<TMessage>>(batchSize);
        try
        {
            Memory<OutboxDeliveryMessage<TMessage>> slice = buffer.AsMemory(0, batchSize);

            return await ProcessMultipleTenants(slice, settings, cancellationToken);

        }
        finally
        {
            arrayPool.Return(buffer);
        }
    }

    private async Task<int> ProcessMultipleTenants<TMessage>(
        Memory<OutboxDeliveryMessage<TMessage>> slice,
        ConsumeSettings settings,
        CancellationToken cancellationToken) where TMessage : IOutboxPayloadMessage
    {
        int count = 0;
        int[] tenantIds = await partCache.GetTenantIds(cancellationToken);

        if (tenantIds.Length == 0)
        {
            return await messageProcessor.ProcessTenantMessages(settings, slice, 0, cancellationToken);
        }

        foreach (int tenantId in tenantIds)
        {
            count += await messageProcessor.ProcessTenantMessages(settings, slice, tenantId, cancellationToken);
        }

        return count;
    }
}
