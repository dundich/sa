using Sa.Outbox.Partitional;
using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes outbox messages in batches until all pending messages are delivered or cancellation is requested.
/// Implements a continuous polling pattern to ensure reliable message delivery.
/// </summary>
internal sealed class DeliveryProcessor(IDeliveryTenant processor, IPartitionalSupportCache partCache) : IDeliveryProcessor
{
    public async Task<long> ProcessMessages<TMessage>(ConsumeSettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        long count = 0;
        bool runAgain;
        do
        {
            int sentCount = await ProcessForEachTenant<TMessage>(settings, cancellationToken);
            runAgain = sentCount > 0;
            count += sentCount;
        }
        while (runAgain && !cancellationToken.IsCancellationRequested);

        return count;
    }

    public async Task<int> ProcessForEachTenant<TMessage>(ConsumeSettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        int batchSize = settings.MaxBatchSize;

        if (batchSize == 0) return 0;

        int count = 0;
        int[] tenantIds = await partCache.GetTenantIds(cancellationToken);

        if (tenantIds.Length == 0)
        {
            return await processor.Process<TMessage>(0, settings, cancellationToken);
        }

        foreach (int tenantId in tenantIds)
        {
            count += await processor.Process<TMessage>(tenantId, settings, cancellationToken);
        }

        return count;
    }
}