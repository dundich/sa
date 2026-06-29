using Sa.Outbox.Partitional;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes outbox messages in batches until all pending messages are delivered or cancellation is requested.
/// Implements a continuous polling pattern to ensure reliable message delivery.
/// </summary>
internal sealed class DeliveryProcessor(
    IDeliveryTenant processor,
    ITenantProvider tenantProvider) : IDeliveryProcessor
{
    public async Task<long> ProcessMessages<TMessage>(
        ConsumerGroupSettings settings,
        CancellationToken cancellationToken)
    {
        // Derive immutable snapshot from mutable bootstrap settings.
        // Cheap operation (~20 property reads) compared to DB/network I/O.
        var canonical = settings.ToCanonical();

        if (canonical.Paused)
        {
            // Consumer group is paused — do not poll.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return 0;
        }

        int batchSize = canonical.MaxBatchSize;
        if (batchSize == 0) return 0;

        int[] tenantIds = await tenantProvider.GetTenantIds(cancellationToken);
        if (tenantIds.Length == 0) return 0;

        long totalProcessed = 0;
        int iterations = 0;

        bool continueProcessing;
        do
        {
            if (iterations > 0 && canonical.IterationDelay > TimeSpan.Zero)
            {
                await Task.Delay(canonical.IterationDelay, cancellationToken);
            }

            int sentCount = await ProcessForEachTenant<TMessage>(tenantIds, settings, canonical, cancellationToken);

            totalProcessed += sentCount;
            iterations++;

            continueProcessing = ShouldContinueProcessing(
                sentCount,
                iterations,
                canonical,
                cancellationToken);
        }
        while (continueProcessing);

        return totalProcessed;
    }

    private static bool ShouldContinueProcessing(
        int lastBatchSize,
        int iterations,
        OutboxConsumerSettings settings,
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

    private async Task<int> ProcessForEachTenant<TMessage>(
        int[] tenantIds,
        ConsumerGroupSettings settings,
        OutboxConsumerSettings canonical,
        CancellationToken cancellationToken)
    {
        return (canonical.PerTenantMaxDegreeOfParallelism == 1)
            ? await ProcessTenantsSequential<TMessage>(tenantIds, settings, canonical, cancellationToken)
            : await ProcessTenantsParallel<TMessage>(tenantIds, settings, canonical, cancellationToken);
    }

    private async Task<int> ProcessTenantsSequential<TMessage>(
        int[] tenantIds,
        ConsumerGroupSettings settings,
        OutboxConsumerSettings canonical,
        CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (int tenantId in tenantIds)
        {
            count += await ProcessInTenant<TMessage>(tenantId, settings, canonical, cancellationToken);
        }

        return count;
    }

    public async Task<int> ProcessTenantsParallel<TMessage>(
        int[] tenantIds,
        ConsumerGroupSettings settings,
        OutboxConsumerSettings canonical,
        CancellationToken cancellationToken)
    {
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = canonical.PerTenantMaxDegreeOfParallelism == -1
                ? Environment.ProcessorCount
                : canonical.PerTenantMaxDegreeOfParallelism,
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
                    int processed = await ProcessInTenant<TMessage>(tenantId, settings, canonical, ct);
                    Interlocked.Add(ref totalCount, processed);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ignore
        }

        return totalCount;
    }

    private async Task<int> ProcessInTenant<TMessage>(
        int tenantId,
        ConsumerGroupSettings settings,
        OutboxConsumerSettings canonical,
        CancellationToken cancellationToken)
    {
        using var tenantCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (canonical.PerTenantTimeout > TimeSpan.Zero)
        {
            tenantCts.CancelAfter(canonical.PerTenantTimeout);
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
