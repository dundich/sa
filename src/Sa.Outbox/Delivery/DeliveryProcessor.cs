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
            // Re-checked every iteration, but deliberately against the *same* immutable snapshot.
            // `settings` was captured once by DeliveryJob<TMessage>.Execute, so a runtime Pause()
            // or Resume() cannot be observed here — by design.
            //
            // Why not re-read IOutboxConsumerManager per iteration: interrupting a drain in flight
            // is more dangerous than letting it finish. A partially drained batch means rented tasks,
            // an in-flight lock renewal and uncommitted per-message statuses, so aborting mid-cycle
            // trades a bounded wait for an unbounded recovery. The wait is bounded by
            // MaxProcessingIterations and by PerTenantTimeout, and Pause() takes effect on the next
            // job execution.
            //
            // Do not "fix" this by injecting IOutboxConsumerManager and polling it here.
            if (settings.Paused)
            {
                await Task.Delay(PausedPollDelay, cancellationToken).ConfigureAwait(false);
                return totalProcessed;
            }

            if (iterations > 0 && settings.IterationDelay > TimeSpan.Zero)
            {
                await Task.Delay(settings.IterationDelay, cancellationToken).ConfigureAwait(false);
            }

            int processed = await ProcessForEachTenant<TMessage>(tenantIds, settings, cancellationToken).ConfigureAwait(false);

            totalProcessed += processed;
            iterations++;

            continueProcessing = ShouldContinueProcessing(
                processed,
                iterations,
                settings,
                cancellationToken);
        }
        while (continueProcessing);

        return totalProcessed;
    }

    private static bool ShouldContinueProcessing(
        int processedCount,
        int iterations,
        OutboxConsumerSettings settings,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        // Driven by the number of messages handled, not successes: a batch where every message
        // failed is still work in flight and the backlog may not be drained yet.
        if (processedCount == 0)
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
        var total = 0;
        foreach (int tenantId in tenantIds)
        {
            total += await ProcessTenantWithTimeout<TMessage>(tenantId, settings, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private async Task<int> ProcessTenantsParallel<TMessage>(
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

        int processed = 0;

        try
        {
            await Parallel.ForEachAsync(
                tenantIds,
                parallelOptions,
                async (tenantId, ct) =>
                {
                    int tenantProcessed = await ProcessTenantWithTimeout<TMessage>(tenantId, settings, ct).ConfigureAwait(false);
                    Interlocked.Add(ref processed, tenantProcessed);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ignore
        }

        return processed;
    }

    /// <summary>
    /// Applies <see cref="OutboxConsumerSettings.PerTenantTimeout"/> to a single tenant pass and
    /// swallows the resulting cancellation, so one unresponsive tenant cannot fail the whole cycle.
    /// Deliberately distinct from <see cref="IDeliveryTenant.ProcessInTenant{TMessage}"/>, which does
    /// the actual work — the two used to share a name, which made the call chain ambiguous to read.
    /// </summary>
    private async Task<int> ProcessTenantWithTimeout<TMessage>(
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
