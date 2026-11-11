using Sa.Outbox.PlugServices;
using System.Buffers;


namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages for a specific tenant with locking and delivery.
/// </summary>
internal sealed class DeliveryTenant(
    IOutboxStorage storage,
    IDeliveryCourier deliveryCourier,
    IDeliveryBatcher batcher,
    FilterFactory filterFactory,
    IOutboxContextFactory contextFactory,
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

        // The storage contracts are keyed by the provider: resolve the manager for this group once
        // per pass and thread it through, instead of resolving it three times.
        IOutboxDeliveryManager deliveryMan = storage.DeliveryManager(settings.ProviderKey);

        var messages = await AcquireMessagesAsync(
            deliveryMan,
            settings,
            filter,
            memoryOwner.Memory[..batchSize],
            cancellationToken);

        if (messages.IsEmpty) return 0;

        var renewal = new RenewalState();

        await using IAsyncDisposable locker = RenewerLocker(deliveryMan, settings, filter, renewal, cancellationToken);

        var delivered = await deliveryCourier.Deliver(settings, filter, messages, cancellationToken);

        // Statuses are written next, and they are what the lock was guarding. From here on a
        // renewal may legitimately renew nothing — the rows leave Processing as soon as the first
        // status lands — so a zero must not be read as a lost lock. Without this the renewer's last
        // tick races ReturnDelivery and aborts a pass that had already delivered its whole batch.
        renewal.MarkCompleting();

        await ReleaseMessagesAsync(deliveryMan, messages, filter, cancellationToken);

        // Only after the statuses are persisted: a context may then be recycled by the factory.
        // If ReleaseMessagesAsync throws, the contexts are left to the GC instead of being pooled.
        ReturnContexts(messages);

        // Handled count, not success count: a batch that failed entirely is still processed
        // work and must not be mistaken for an empty queue by the greedy loop.
        return delivered;
    }

    private void ReturnContexts<TMessage>(ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages)
    {
        foreach (IOutboxContextOperations<TMessage> context in messages.Span)
        {
            contextFactory.ReturnToPool(context);
        }
    }

    private OutboxMessageFilter CreateFilter<TMessage>(int tenantId, OutboxConsumerSettings settings)
    {
        return filterFactory.CreateFilter<TMessage>(
            tenantId: tenantId,
            consumerGroupId: settings.ConsumerGroupId,
            now: GetUtcNow(),
            lookbackInterval: settings.LookbackInterval,
            batchingWindow: settings.BatchingWindow,
            providerKey: settings.ProviderKey);
    }

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    private async Task<int> CalculateBatchSizeAsync(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        var calculatedSize = await batcher.CalculateBatchSizeAsync(
            settings.MaxBatchSize,
            filter,
            cancellationToken);

        return Math.Clamp(calculatedSize, 0, settings.MaxBatchSize);
    }

    private static async Task<ReadOnlyMemory<IOutboxContextOperations<TMessage>>> AcquireMessagesAsync<TMessage>(
        IOutboxDeliveryManager deliveryMan,
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
        IOutboxDeliveryManager deliveryMan,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        return deliveryMan.ReturnDelivery(
            messages,
            filter with { NowDate = GetUtcNow() },
            cancellationToken);
    }

    private LockRenewerHandle RenewerLocker(
        IOutboxDeliveryManager deliveryMan,
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        RenewalState renewal,
        CancellationToken cancellationToken)
            => LockRenewer.KeepLocked(
                settings.LockRenewal
                , async t =>
                {
                    var nowDate = GetUtcNow();
                    int renewed = await deliveryMan.ExtendDeliveryAsync(
                        settings.LockDuration
                        , filter with { NowDate = nowDate }
                        , t).ConfigureAwait(false);

                    // Zero renewed rows is the provider's way of saying the lease is no longer
                    // ours: the batch was re-rented, or task_transact_id was overwritten. This is
                    // the only signal there is — the renewer itself fails on a *throwing* delegate
                    // only — so ignoring it let the consumer keep working (and later acknowledging)
                    // under a lock somebody else already owned. Reported as a lost lock, which the
                    // per-tenant pass already absorbs, instead of as a delivery failure.
                    //
                    // Except during the release itself: once the pass has finished consuming, the
                    // rows stop being Processing and a renewal legitimately matches nothing.
                    if (renewed == 0 && !t.IsCancellationRequested && !renewal.IsCompleting)
                    {
                        throw new LockRenewalException(
                            $"Lock renewal matched none of the messages of consumer group "
                            + $"'{filter.ConsumerGroupId}' (tenant {filter.TenantId}, transaction "
                            + $"'{filter.TransactId}'): the batch was re-rented by another worker.");
                    }
                }
                , cancellationToken: cancellationToken);

    /// <summary>
    /// Whether this pass has finished consuming and is writing the delivery statuses. Set before
    /// <c>ReturnDelivery</c>: the rows are terminal from then on, so a renewal that matches nothing
    /// is the expected outcome rather than a stolen lease.
    /// </summary>
    private sealed class RenewalState
    {
        private volatile bool _completing;

        public bool IsCompleting => _completing;

        public void MarkCompleting() => _completing = true;
    }

    private static ClearingMemoryOwner<IOutboxContextOperations<TMessage>> RentMemory<TMessage>(int size)
        => new ClearingMemoryOwner<IOutboxContextOperations<TMessage>>(
            MemoryPool<IOutboxContextOperations<TMessage>>.Shared.Rent(size));

    /// <summary>
    /// <see cref="IMemoryOwner{T}"/> decorator that clears the rented array before handing it back
    /// to the pool.
    /// <para>
    /// Without this, the pooled array keeps references to the contexts of the last delivered batch —
    /// and through them to the message payloads and exceptions — until the very same slots happen to
    /// be overwritten. The tail region <c>[batchSize..rented)</c> is never written at all, so it
    /// would retain whatever the previous rental left there. Both effects pin whole batches in
    /// memory for as long as the pool entry stays idle.
    /// </para>
    /// </summary>
    private sealed class ClearingMemoryOwner<T>(IMemoryOwner<T> inner) : IMemoryOwner<T>
    {
        public Memory<T> Memory => inner.Memory;

        public void Dispose()
        {
            // Span.Clear() covers exactly the rented region, whether or not it is backed by a
            // larger array with a non-zero offset.
            inner.Memory.Span.Clear();

            inner.Dispose();
        }
    }
}
