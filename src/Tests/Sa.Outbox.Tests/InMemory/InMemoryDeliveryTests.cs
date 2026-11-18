using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests.InMemory;

/// <summary>
/// Delivery-side behaviour of the in-memory provider: leases, outcomes, attempt accounting and
/// tenant discovery. This is the contract a provider author has to get right.
/// </summary>
public class InMemoryDeliveryTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Return_Ok_SettlesTheMessage()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await PublishAsync(provider, clock, "done");

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(1);
        int rented = await RentAsync(provider, clock, buffer, "tx-1");
        Assert.Equal(1, rented);

        buffer[0].Ok();
        int applied = await provider.Manager().ReturnDelivery<TestMessage>(
            buffer.AsMemory(0, rented),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken);

        Assert.Equal(1, applied);
        Assert.Equal(0, await RentAsync(provider, clock, buffer, "tx-2"));
    }

    [Fact]
    public async Task Return_Retry_HonoursTheDelayAndAdvancesTheAttempt()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await PublishAsync(provider, clock, "retry-me");

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(1);
        await RentAsync(provider, clock, buffer, "tx-1");

        buffer[0].Retry(TimeSpan.FromSeconds(5));
        await provider.Manager().ReturnDelivery<TestMessage>(
            buffer.AsMemory(0, 1),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken);

        Assert.Equal(0, await RentAsync(provider, clock, buffer, "tx-2"));

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await RentAsync(provider, clock, buffer, "tx-3"));
        Assert.Equal(1, buffer[0].DeliveryInfo.Attempt);
    }

    [Fact]
    public async Task Return_Postpone_HonoursTheDelayWithoutAdvancingTheAttempt()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await PublishAsync(provider, clock, "postpone-me");

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(1);
        await RentAsync(provider, clock, buffer, "tx-1");

        buffer[0].Postpone(TimeSpan.FromSeconds(5));
        await provider.Manager().ReturnDelivery<TestMessage>(
            buffer.AsMemory(0, 1),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken);

        Assert.Equal(0, await RentAsync(provider, clock, buffer, "tx-2"));

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await RentAsync(provider, clock, buffer, "tx-3"));
        Assert.Equal(0, buffer[0].DeliveryInfo.Attempt);
    }

    [Fact]
    public async Task ExpiredLease_IsRentedAgainWithANewGeneration()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await PublishAsync(provider, clock, "lease");

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(1);
        await RentAsync(provider, clock, buffer, "tx-1", lockDuration: TimeSpan.FromSeconds(10));
        OutboxRentToken firstToken = buffer[0].RentToken;

        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(1, await RentAsync(provider, clock, buffer, "tx-2", lockDuration: TimeSpan.FromSeconds(10)));
        Assert.NotEqual(firstToken, buffer[0].RentToken);

        Assert.Equal(0, await provider.Manager().ExtendDeliveryAsync(
            TimeSpan.FromSeconds(10),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken));

        Assert.Equal(1, await provider.Manager().ExtendDeliveryAsync(
            TimeSpan.FromSeconds(10),
            InMemoryTestHost.Filter<TestMessage>("tx-2", now: clock.Now),
            TestToken));
    }

    [Fact]
    public async Task Return_FromAStaleOwner_IsIgnored()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await PublishAsync(provider, clock, "stale");

        IOutboxContextOperations<TestMessage>[] stale = InMemoryTestHost.RentBuffer<TestMessage>(1);
        await RentAsync(provider, clock, stale, "tx-1", lockDuration: TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(11));

        IOutboxContextOperations<TestMessage>[] fresh = InMemoryTestHost.RentBuffer<TestMessage>(1);
        Assert.Equal(1, await RentAsync(provider, clock, fresh, "tx-2", lockDuration: TimeSpan.FromSeconds(10)));

        stale[0].Ok();
        int applied = await provider.Manager().ReturnDelivery<TestMessage>(
            stale.AsMemory(0, 1),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken);

        Assert.Equal(0, applied);

        // The new owner still holds the lease.
        Assert.Equal(1, await provider.Manager().ExtendDeliveryAsync(
            TimeSpan.FromSeconds(10),
            InMemoryTestHost.Filter<TestMessage>("tx-2", now: clock.Now),
            TestToken));
    }

    [Fact]
    public async Task ExtendDelivery_ReturnsTheNumberOfExtendedLeases()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("a"), new TestMessage("b") }, createdAt: clock.Now),
            TestToken);

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(2);
        Assert.Equal(2, await RentAsync(provider, clock, buffer, "tx-1"));

        Assert.Equal(2, await provider.Manager().ExtendDeliveryAsync(
            TimeSpan.FromSeconds(30),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken));

        foreach (IOutboxContextOperations<TestMessage> context in buffer)
            context.Ok();

        await provider.Manager().ReturnDelivery<TestMessage>(
            buffer.AsMemory(0, 2),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken);

        Assert.Equal(0, await provider.Manager().ExtendDeliveryAsync(
            TimeSpan.FromSeconds(30),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken));
    }

    [Fact]
    public async Task Detector_ReturnsOnlyTenantsWithDeliverableMessages()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("t1") }, tenantId: 1, createdAt: clock.Now),
            TestToken);

        await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("t2") }, tenantId: 2, createdAt: clock.Now),
            TestToken);

        Assert.Equal(new[] { 1, 2 }, await provider.Detector().GetTenantIdsAsync(TestToken));

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(1);
        await RentAsync(provider, clock, buffer, "tx-1", tenantId: 1);
        buffer[0].Ok();
        await provider.Manager().ReturnDelivery<TestMessage>(
            buffer.AsMemory(0, 1),
            InMemoryTestHost.Filter<TestMessage>("tx-1", tenantId: 1, now: clock.Now),
            TestToken);

        Assert.Equal(new[] { 2 }, await provider.Detector().GetTenantIdsAsync(TestToken));
    }

    [Fact]
    public async Task NewProviderInstance_StartsEmpty()
    {
        var clock = new TestClock(InMemoryTestHost.Start);

        using (ServiceProvider first = InMemoryTestHost.Build(clock: clock))
            await PublishAsync(first, clock, "lost");

        using ServiceProvider restarted = InMemoryTestHost.Build(clock: clock);

        Assert.Equal(0, restarted.Store().Count);
    }

    private static Task<ulong> PublishAsync(ServiceProvider provider, TestClock clock, string value)
        => provider.Writer()
            .InsertBulk(InMemoryTestHost.Batch(new[] { new TestMessage(value) }, createdAt: clock.Now), TestToken)
            .AsTask();

    private static Task<int> RentAsync(
        ServiceProvider provider,
        TestClock clock,
        IOutboxContextOperations<TestMessage>[] buffer,
        string transactId,
        TimeSpan? lockDuration = null,
        int tenantId = 1)
            => provider.Manager().RentDelivery<TestMessage>(
                buffer,
                lockDuration ?? TimeSpan.FromSeconds(30),
                InMemoryTestHost.Filter<TestMessage>(transactId, tenantId: tenantId, now: clock.Now),
                TestToken);
}
