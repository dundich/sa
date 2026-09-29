using Sa.Classes;
using Sa.Outbox.Delivery;
using Sa.Outbox.Partitional;

namespace Sa.Outbox.Tests;

/// <summary>
/// Covers how <see cref="DeliveryProcessor"/> treats a failure coming out of
/// <see cref="IDeliveryTenant.ProcessInTenant{TMessage}"/>.
/// <para>
/// The one case that has to be absorbed is <see cref="LockRenewalException"/>. The renewer raises it
/// out of its own dispose, which happens after the batch has already been delivered and released —
/// the work is done and the lock is simply gone. Letting it escape aborted the tenant loop, voided
/// the counts already accumulated for the tenants processed before it, and surfaced at the scheduler
/// as a job error, so one tenant's lost lock failed the whole cycle for every tenant.
/// </para>
/// <para>
/// Everything else still has to propagate: an exception escaping the per-tenant pass comes from the
/// infrastructure under it (storage, batching, filter construction), and a cycle that swallowed
/// those would report an empty queue while nothing is being delivered at all.
/// </para>
/// </summary>
public class DeliveryProcessorTests
{
    private sealed class TestMessage { }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static OutboxConsumerSettings CreateSettings(
        int perTenantMaxDegreeOfParallelism = 1,
        int maxProcessingIterations = 1)
        => new(
            ConsumerGroupId: "test-group", AsSingleton: false,
            Interval: TimeSpan.FromMinutes(1), InitialDelay: TimeSpan.Zero,
            ConcurrencyLimit: 1, MaxConcurrency: 1, RetryCountOnError: 0,
            MaxBatchSize: 16, MaxProcessingIterations: maxProcessingIterations, IterationDelay: TimeSpan.Zero,
            LockDuration: TimeSpan.FromSeconds(10), LockRenewal: TimeSpan.FromSeconds(3),
            LookbackInterval: TimeSpan.FromDays(7), MaxDeliveryAttempts: 3,
            BatchingWindow: TimeSpan.FromSeconds(3), PerTenantTimeout: TimeSpan.Zero,
            PerTenantMaxDegreeOfParallelism: perTenantMaxDegreeOfParallelism, Paused: false, Version: 0);

    private static DeliveryProcessor CreateProcessor(int[] tenantIds, Func<int, Task<int>> onProcess)
        => new(new FakeDeliveryTenant(onProcess), new FakeTenantProvider(tenantIds));

    private static Task<int> LoseLock(int _) =>
        throw new LockRenewalException("lost", new InvalidOperationException("storage is gone"));

    [Fact]
    public async Task LostLockOnOneTenantKeepsTheCountsOfTheOthers()
    {
        var processor = CreateProcessor([1, 2], tenantId => tenantId == 1 ? LoseLock(tenantId) : Task.FromResult(3));

        long processed = await processor.ProcessMessages<TestMessage>(CreateSettings(), TestToken);

        Assert.Equal(3, processed);
    }

    [Fact]
    public async Task LostLockOnEveryTenantIsReportedAsAnEmptyCycle()
    {
        var processor = CreateProcessor([1, 2], LoseLock);

        long processed = await processor.ProcessMessages<TestMessage>(CreateSettings(), TestToken);

        Assert.Equal(0, processed);
    }

    /// <summary>
    /// The parallel pass failed differently from the sequential one: the exception came out of the
    /// loop body, so it also stopped the tenants that were still in flight and voided their counts
    /// on top of breaking the cycle.
    /// </summary>
    [Fact]
    public async Task LostLockKeepsTheCountsOfTheOthersInTheParallelPass()
    {
        var processor = CreateProcessor([1, 2], tenantId => tenantId == 1 ? LoseLock(tenantId) : Task.FromResult(3));

        long processed = await processor.ProcessMessages<TestMessage>(
            CreateSettings(perTenantMaxDegreeOfParallelism: 2), TestToken);

        Assert.Equal(3, processed);
    }

    [Fact]
    public async Task StorageFailureStillFailsTheCycle()
    {
        var processor = CreateProcessor([1, 2], _ => throw new InvalidOperationException("storage is gone"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessMessages<TestMessage>(CreateSettings(), TestToken));

        Assert.Equal("storage is gone", ex.Message);
    }

    /// <summary>
    /// The parallel pass stops every other in-flight tenant on the first failure, so the escaping
    /// exception is the one the failing tenant raised — <c>Parallel.ForEachAsync</c> unwraps a
    /// single failure rather than wrapping it. Asserted, not assumed: the catch that keeps a lost
    /// lock local must not turn into one that also flattens the diagnostics of a real failure.
    /// </summary>
    [Fact]
    public async Task StorageFailureStillFailsTheCycleInTheParallelPass()
    {
        var processor = CreateProcessor([1, 2], _ => throw new InvalidOperationException("storage is gone"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessMessages<TestMessage>(
                CreateSettings(perTenantMaxDegreeOfParallelism: 2), TestToken));

        Assert.Equal("storage is gone", ex.Message);
    }

    /// <summary>
    /// A per-tenant timeout has always been absorbed as 0, and a lost lock has to be
    /// indistinguishable from it at this level — whether the lock is really gone is the renewer's
    /// knowledge to report, not the cycle's to re-derive.
    /// </summary>
    [Fact]
    public async Task TenantCancellationIsAbsorbedJustLikeALostLock()
    {
        var processor = CreateProcessor([1], _ => throw new OperationCanceledException());

        long processed = await processor.ProcessMessages<TestMessage>(CreateSettings(), TestToken);

        Assert.Equal(0, processed);
    }

    private sealed class FakeDeliveryTenant(Func<int, Task<int>> onProcess) : IDeliveryTenant
    {
        public Task<int> ProcessInTenant<TMessage>(
            int tenantId,
            OutboxConsumerSettings settings,
            CancellationToken cancellationToken)
            => onProcess(tenantId);
    }

    // NB: the namespace is Sa.Outbox.Partitional, with a doubled "ti" — folder and namespace
    // alike. Neither spelling contains the substring "Partial", so grepping for "Partial"
    // finds nothing here, which is a reliable way to waste an afternoon.
    private sealed class FakeTenantProvider(int[] tenantIds) : ITenantProvider
    {
        public ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken) => ValueTask.FromResult(tenantIds);
    }
}
