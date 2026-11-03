using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.Exceptions;

namespace Sa.Outbox.Tests.InMemory;

/// <summary>
/// Publish-side behaviour of the in-memory provider: what is stored, in which order, and what is
/// refused.
/// </summary>
public class InMemoryPublishTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InsertBulk_StoresEveryMessageAndReturnsTheCount()
    {
        using ServiceProvider provider = InMemoryTestHost.Build();

        ulong stored = await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("a"), new TestMessage("b"), new TestMessage("c") }),
            TestToken);

        Assert.Equal(3UL, stored);
        Assert.Equal(3, provider.Store().Count);
    }

    [Fact]
    public async Task InsertBulk_WithPublishContext_Throws()
    {
        using ServiceProvider provider = InMemoryTestHost.Build();

        await Assert.ThrowsAsync<NotSupportedException>(async () => await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("a") }),
            context: new object(),
            TestToken));
    }

    [Fact]
    public async Task InsertBulk_OverCapacity_ThrowsAndStoresNothing()
    {
        using ServiceProvider provider = InMemoryTestHost.Build(settings => settings.Capacity = 2);

        await Assert.ThrowsAsync<OutboxException>(async () => await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("a"), new TestMessage("b"), new TestMessage("c") }),
            TestToken));

        Assert.Equal(0, provider.Store().Count);
    }

    [Fact]
    public async Task Rent_ReturnsMessagesInPublishOrder()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(
                new[] { new TestMessage("first"), new TestMessage("second"), new TestMessage("third") },
                createdAt: clock.Now),
            TestToken);

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(3);

        int rented = await provider.Manager().RentDelivery<TestMessage>(
            buffer,
            TimeSpan.FromSeconds(30),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now),
            TestToken);

        Assert.Equal(3, rented);
        Assert.Equal(new[] { "first", "second", "third" }, buffer.Take(rented).Select(c => c.Payload.Value));
    }

    [Fact]
    public async Task Rent_DoesNotCrossTenantPartOrPayloadType()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("tenant-1") }, tenantId: 1, createdAt: clock.Now),
            TestToken);

        await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("tenant-2") }, tenantId: 2, createdAt: clock.Now),
            TestToken);

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(4);

        int rented = await provider.Manager().RentDelivery<TestMessage>(
            buffer,
            TimeSpan.FromSeconds(30),
            InMemoryTestHost.Filter<TestMessage>("tx-1", tenantId: 2, now: clock.Now),
            TestToken);

        Assert.Equal(1, rented);
        Assert.Equal("tenant-2", buffer[0].Payload.Value);
    }

    [Fact]
    public async Task Rent_RespectsTheVisibilityWindow()
    {
        var clock = new TestClock(InMemoryTestHost.Start);
        using ServiceProvider provider = InMemoryTestHost.Build(clock: clock);

        await provider.Writer().InsertBulk(
            InMemoryTestHost.Batch(new[] { new TestMessage("too-fresh") }, createdAt: clock.Now),
            TestToken);

        IOutboxContextOperations<TestMessage>[] buffer = InMemoryTestHost.RentBuffer<TestMessage>(1);

        // The pipeline's ToDate is "now - BatchingWindow", so a message published this instant is
        // still invisible.
        int rented = await provider.Manager().RentDelivery<TestMessage>(
            buffer,
            TimeSpan.FromSeconds(30),
            InMemoryTestHost.Filter<TestMessage>("tx-1", now: clock.Now) with { ToDate = clock.Now - TimeSpan.FromSeconds(3) },
            TestToken);

        Assert.Equal(0, rented);
    }
}
