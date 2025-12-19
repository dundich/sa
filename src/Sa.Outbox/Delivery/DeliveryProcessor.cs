using Sa.Outbox.Partitional;
using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes outbox messages in batches until all pending messages are delivered or cancellation is requested.
/// Implements a continuous polling pattern to ensure reliable message delivery.
/// </summary>
internal sealed class DeliveryProcessor(IDeliveryTenant processor, IPartitionalSupportCache partCache) : IDeliveryProcessor
{
    public async Task<long> ProcessMessages<TMessage>(ConsumerGroupSettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        long totalProcessed = 0;
        int iterations = 0;

        bool continueProcessing;

        do
        {
            if (iterations > 0 && settings.ConsumeSettings.IterationDelay > TimeSpan.Zero)
            {
                await Task.Delay(settings.ConsumeSettings.IterationDelay, cancellationToken);
            }

            int sentCount = await ProcessForEachTenant<TMessage>(settings, cancellationToken);

            totalProcessed += sentCount;
            iterations++;

            continueProcessing = ShouldContinueProcessing(
                sentCount,
                iterations,
                settings.ConsumeSettings,
                cancellationToken);
        }
        while (continueProcessing);

        return totalProcessed;
    }

    private static bool ShouldContinueProcessing(
        int lastBatchSize,
        int iterations,
        ConsumeSettings settings,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        if (lastBatchSize == 0)
            return false;

        if (settings.MaxProcessingIterations >= 0 && iterations >= settings.MaxProcessingIterations)
            return false;

        return true;
    }

    public async Task<int> ProcessForEachTenant<TMessage>(ConsumerGroupSettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        int batchSize = settings.ConsumeSettings.MaxBatchSize;

        if (batchSize == 0) return 0;


        int[] tenantIds = await partCache.GetTenantIds(cancellationToken);

        if (tenantIds.Length == 0)
        {
            return await ProcessInTenant<TMessage>(0, settings, cancellationToken);
        }

        if (settings.ConsumeSettings.PerTenantMaxDegreeOfParallelism == 1)
        {
            return await ProcessTenantsSequential<TMessage>(tenantIds, settings, cancellationToken);
        }

        return await ProcessTenantsParallel<TMessage>(tenantIds, settings, cancellationToken);
    }

    private async Task<int> ProcessTenantsSequential<TMessage>(int[] tenantIds, ConsumerGroupSettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        int count = 0;
        foreach (int tenantId in tenantIds)
        {
            count += await ProcessInTenant<TMessage>(tenantId, settings, cancellationToken);
        }

        return count;
    }

    public async Task<int> ProcessTenantsParallel<TMessage>(
        int[] tenantIds,
        ConsumerGroupSettings settings,
        CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = settings.ConsumeSettings.PerTenantMaxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        int totalCount = 0;

        try
        {
            await Parallel.ForEachAsync(
                tenantIds,
                parallelOptions,
                async (tenantId, ct) =>
                {
                    int processed = await ProcessInTenant<TMessage>(tenantId, settings, cancellationToken);
                    Interlocked.Add(ref totalCount, processed);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ignore
        }

        return totalCount;
    }

    private async Task<int> ProcessInTenant<TMessage>(int tenantId, ConsumerGroupSettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        using var tenantCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (settings.ConsumeSettings.PerTenantTimeout > TimeSpan.Zero)
        {
            tenantCts.CancelAfter(settings.ConsumeSettings.PerTenantTimeout);
        }

        try
        {
            return await processor.ProcessInTenant<TMessage>(tenantId, settings, tenantCts.Token);
        }
        catch (OperationCanceledException)
        {
            // ignore
            return 0;
        }
    }
}