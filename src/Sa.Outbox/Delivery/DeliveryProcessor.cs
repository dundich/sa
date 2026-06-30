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
    /// <summary>
    /// Delay when consumer group is paused — avoids busy-waiting on repeated polls.
    /// </summary>
    private static readonly TimeSpan PausedPollDelay = TimeSpan.FromSeconds(5);

    public async Task<long> ProcessMessages<TMessage>(
        OutboxConsumerSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Paused)
        {
            // Consumer group is paused — do not poll.
            await Task.Delay(PausedPollDelay, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        int batchSize = settings.MaxBatchSize;
        if (batchSize == 0) return 0;

        int[] tenantIds = await tenantProvider.GetTenantIds(cancellationToken).ConfigureAwait(false);
        if (tenantIds.Length == 0) return 0;

        long totalProcessed = 0;
        int iterations = 0;

        bool continueProcessing;
        do
        {
            // Re-check Paused on each iteration — a runtime Pause() should interrupt
            // the greedy loop, not wait for all pending messages to drain.
            if (settings.Paused)
            {
                await Task.Delay(PausedPollDelay, cancellationToken).ConfigureAwait(false);
                return totalProcessed;
            }

            if (iterations > 0 && settings.IterationDelay > TimeSpan.Zero)
            {
                await Task.Delay(settings.IterationDelay, cancellationToken).ConfigureAwait(false);
            }

            int sentCount = await ProcessForEachTenant<TMessage>(tenantIds, settings, cancellationToken).ConfigureAwait(false);

            totalProcessed += sentCount;
            iterations++;

            continueProcessing = ShouldContinueProcessing(
                sentCount,
                iterations,
                settings,
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
        OutboxConsumerSettings settings,
        CancellationToken cancellationToken)
    {
        return (settings.PerTenantMaxDegreeOfParallelism == 1)
            ? await ProcessTenantsSequential<TMessage>(tenantIds, settings, cancellationToken).ConfigureAwait(false)
            : await ProcessTenantsParallel<TMessage>(tenantIds, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ProcessTenantsSequential<TMessage>(
        int[] tenantIds,
        OutboxConsumerSettings settings,
        CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (int tenantId in tenantIds)
        {
            count += await ProcessInTenant<TMessage>(tenantId, settings, cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    public async Task<int> ProcessTenantsParallel<TMessage>(
        int[] tenantIds,
        OutboxConsumerSettings settings,
        CancellationToken cancellationToken)
    {
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = settings.PerTenantMaxDegreeOfParallelism == -1
                ? Environment.ProcessorCount
                : settings.PerTenantMaxDegreeOfParallelism,
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
                    int processed = await ProcessInTenant<TMessage>(tenantId, settings, ct).ConfigureAwait(false);
                    Interlocked.Add(ref totalCount, processed);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ignore
        }

        return totalCount;
    }

    private async Task<int> ProcessInTenant<TMessage>(
        int tenantId,
        OutboxConsumerSettings settings,
        CancellationToken cancellationToken)
    {
        using var tenantCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (settings.PerTenantTimeout > TimeSpan.Zero)
        {
            tenantCts.CancelAfter(settings.PerTenantTimeout);
        }

        try
        {
            return await processor.ProcessInTenant<TMessage>(tenantId, settings, tenantCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
            return 0;
        }
    }
}
