using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis.Configuration;
using StackExchange.Redis;

namespace Sa.Outbox.RedisTests;

public class OutboxRedisTests(OutboxRedisFixture fixture) : IClassFixture<OutboxRedisFixture>
{
    private static int s_tenant = 1000;

    private static int NextTenant() => Interlocked.Increment(ref s_tenant);

    private IServiceProvider Services => fixture.ServiceProvider;

    private IDatabase Database => Services.GetRequiredService<IDatabase>();

    private IOutboxDeliveryManager Manager => Services.GetRequiredService<IOutboxDeliveryManager>();

    private RedisOutboxSettings Settings => Services.GetRequiredService<RedisOutboxSettings>();

    [Fact]
    public async Task Health_ReportsTheProviderHealthy()
    {
        IReadOnlyList<OutboxProviderHealthReport> reports =
            await Services.GetRequiredService<IOutboxHealth>().CheckAsync(TestContext.Current.CancellationToken);

        OutboxProviderHealthReport report = Assert.Single(reports);
        Assert.True(report.Healthy, report.Reason);
        Assert.Equal(Sa.Outbox.OutboxScope.DefaultKey, report.ProviderKey);
    }

    [Fact]
    public async Task PublishRentFinish_Success_AcknowledgesMessages()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();
        ulong published = await publisher.Publish(
            [new TestMessage("a", 1), new TestMessage("b", 2), new TestMessage("c", 3)], tenant, ct);

        Assert.Equal(3UL, published);

        OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "success-group");
        IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[8];

        int rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct);

        Assert.Equal(3, rented);
        Assert.Equal(["a", "b", "c"], buffer.Take(rented).Select(m => m.Payload.PayloadId));
        Assert.All(buffer.Take(rented), m => Assert.Equal(0, m.DeliveryInfo.Attempt));

        foreach (IOutboxContextOperations<TestMessage> context in buffer.Take(rented))
            context.Ok();

        int finished = await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, rented), filter, ct);
        Assert.Equal(3, finished);

        // The group cursor has moved past every entry and none is due, so a second poll finds nothing.
        OutboxMessageFilter next = filter with { TransactId = Guid.CreateVersion7().ToString("N"), NowDate = DateTimeOffset.UtcNow };
        int again = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), next, ct);
        Assert.Equal(0, again);

        // Acknowledged entries leave no pending messages behind.
        StreamPendingInfo pending = await Database.StreamPendingAsync(StreamKey(tenant, filter), filter.ConsumerGroupId);
        Assert.Equal(0, pending.PendingMessageCount);
    }

    [Fact]
    public async Task Postpone_LeavesTheEntryPendingUntilClaimIdle()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();
        await publisher.Publish([new TestMessage("later")], tenant, ct);

        try
        {
            Settings.ConsumeSettings.PendingClaimIdle = TimeSpan.FromMilliseconds(100);

            OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "postpone-group");
            IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

            int rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct);
            Assert.Equal(1, rented);

            buffer[0].Postpone(TimeSpan.FromMinutes(5), "not yet");
            int finished = await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, rented), filter, ct);
            Assert.Equal(1, finished);

            // Postponing writes nothing: the entry simply stays in the pending entries list.
            StreamPendingInfo pending = await Database.StreamPendingAsync(StreamKey(tenant, filter), filter.ConsumerGroupId);
            Assert.Equal(1, pending.PendingMessageCount);

            // A poll right away must not claim it — the entry has not been idle long enough yet.
            OutboxMessageFilter immediate = OutboxRedisFixture.CreateFilter<TestMessage>(
                tenant, filter.ConsumerGroupId, DateTimeOffset.UtcNow);

            Assert.Equal(0, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), immediate, ct));

            await Task.Delay(250, ct);

            // Once the idle threshold passes, XAUTOCLAIM hands it to the next delivery call and the
            // delivery counter (not a provider-side hash) reports the new attempt.
            OutboxMessageFilter later = OutboxRedisFixture.CreateFilter<TestMessage>(
                tenant, filter.ConsumerGroupId, DateTimeOffset.UtcNow);

            rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), later, ct);
            Assert.Equal(1, rented);
            Assert.Equal(1, buffer[0].DeliveryInfo.Attempt);
        }
        finally
        {
            Settings.ConsumeSettings.PendingClaimIdle = RedisOutboxConsumeSettings.Defaults.PendingClaimIdle;
        }
    }

    [Fact]
    public async Task Warn_LeavesTheEntryPendingAndIncrementsTheAttemptOnTheNextClaim()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();
        await publisher.Publish([new TestMessage("flaky")], tenant, ct);

        try
        {
            Settings.ConsumeSettings.PendingClaimIdle = TimeSpan.FromMilliseconds(100);

            OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "warn-group");
            IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

            int rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct);
            Assert.Equal(1, rented);
            Assert.Equal(0, buffer[0].DeliveryInfo.Attempt);

            buffer[0].Warn(new InvalidOperationException("temporary"), null, TimeSpan.Zero);
            await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, rented), filter, ct);

            await Task.Delay(250, ct);

            // A warning is a failed delivery, so the next claim reports attempt 1.
            OutboxMessageFilter later = OutboxRedisFixture.CreateFilter<TestMessage>(
                tenant, filter.ConsumerGroupId, DateTimeOffset.UtcNow);

            rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), later, ct);
            Assert.Equal(1, rented);
            Assert.Equal(1, buffer[0].DeliveryInfo.Attempt);
        }
        finally
        {
            Settings.ConsumeSettings.PendingClaimIdle = RedisOutboxConsumeSettings.Defaults.PendingClaimIdle;
        }
    }

    [Fact]
    public async Task AbandonedBatch_IsClaimedAfterPendingClaimIdle()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();
        await publisher.Publish([new TestMessage("orphan")], tenant, ct);

        try
        {
            Settings.ConsumeSettings.PendingClaimIdle = TimeSpan.FromMilliseconds(100);

            OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "orphan-group");
            IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

            // The worker that rents this message never finishes, exactly like a crashed instance.
            Assert.Equal(1, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct));

            await Task.Delay(250, ct);

            OutboxMessageFilter later = OutboxRedisFixture.CreateFilter<TestMessage>(
                tenant, filter.ConsumerGroupId, DateTimeOffset.UtcNow);

            int rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), later, ct);

            Assert.Equal(1, rented);
            Assert.Equal("orphan", buffer[0].Payload.PayloadId);
            Assert.Equal(1, buffer[0].DeliveryInfo.Attempt);
        }
        finally
        {
            Settings.ConsumeSettings.PendingClaimIdle = RedisOutboxConsumeSettings.Defaults.PendingClaimIdle;
        }
    }

    [Fact]
    public async Task PoisonMessage_IsDroppedAfterMaxDeliveryAttempts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();
        await publisher.Publish([new TestMessage("poison")], tenant, ct);

        try
        {
            Settings.ConsumeSettings.PendingClaimIdle = TimeSpan.FromMilliseconds(50);
            Settings.ConsumeSettings.MaxDeliveryAttempts = 1;

            OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "poison-group");
            IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];
            RedisKey streamKey = StreamKey(tenant, filter);

            // First delivery. The worker never calls ReturnDelivery — a consumer that crashes on
            // this payload would leave it pending forever.
            Assert.Equal(1, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct));

            await Task.Delay(250, ct);

            // The second claim sees delivery count 2 > MaxDeliveryAttempts 1: the default poison
            // handler logs and the provider acknowledges, never handing it to the consumer again.
            OutboxMessageFilter later = OutboxRedisFixture.CreateFilter<TestMessage>(
                tenant, filter.ConsumerGroupId, DateTimeOffset.UtcNow);

            Assert.Equal(0, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), later, ct));

            StreamPendingInfo pending = await Database.StreamPendingAsync(streamKey, filter.ConsumerGroupId);
            Assert.Equal(0, pending.PendingMessageCount);

            // No dead-letter stream: the entry itself stays in the message stream until retention
            // trims it, so it is still available for inspection.
            Assert.Equal(1, await Database.StreamLengthAsync(streamKey));
        }
        finally
        {
            Settings.ConsumeSettings.PendingClaimIdle = RedisOutboxConsumeSettings.Defaults.PendingClaimIdle;
            Settings.ConsumeSettings.MaxDeliveryAttempts = RedisOutboxConsumeSettings.Defaults.MaxDeliveryAttempts;
        }
    }

    [Fact]
    public async Task PermanentError_IsDroppedAndAcknowledged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();
        await publisher.Publish([new TestMessage("broken", 42)], tenant, ct);

        OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "dropped-group");
        IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

        int rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct);
        Assert.Equal(1, rented);

        buffer[0].Error(new InvalidOperationException("boom"), "permanent");
        int finished = await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, rented), filter, ct);
        Assert.Equal(1, finished);

        // The default poison handler drops it; the entry is acknowledged, never re-delivered.
        StreamPendingInfo pending = await Database.StreamPendingAsync(StreamKey(tenant, filter), filter.ConsumerGroupId);
        Assert.Equal(0, pending.PendingMessageCount);

        OutboxMessageFilter later = OutboxRedisFixture.CreateFilter<TestMessage>(
            tenant, filter.ConsumerGroupId, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(0, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), later, ct));
    }

    [Fact]
    public async Task ExtendDelivery_ResetsIdleTimeWithoutConsumingAnAttempt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();
        await publisher.Publish([new TestMessage("slow")], tenant, ct);

        OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "extend-group");
        IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

        Assert.Equal(1, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromSeconds(5), filter, ct));

        await Task.Delay(500, ct);

        // The renewal addresses the same consumer (TransactId), resets the idle time and must not
        // count as another delivery.
        int extended = await Manager.ExtendDelivery(TimeSpan.FromMinutes(10), filter, ct);
        Assert.Equal(1, extended);

        StreamPendingMessageInfo[] pending = await Database.StreamPendingMessagesAsync(
            StreamKey(tenant, filter),
            filter.ConsumerGroupId,
            count: 10,
            consumerName: filter.TransactId,
            minId: null,
            maxId: null);

        Assert.Single(pending);
        Assert.Equal(1, pending[0].DeliveryCount);
        Assert.True(
            pending[0].IdleTimeInMilliseconds < 400,
            "the renewal must reset the idle clock, not merely leave it where it was");
    }

    [Fact]
    public async Task TenantDetector_ReportsEveryPublishedTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int first = NextTenant();
        int second = NextTenant();

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();
        await publisher.Publish([new TestMessage("1")], first, ct);
        await publisher.Publish([new TestMessage("2")], second, ct);

        IOutboxTenantDetector detector = Services.GetRequiredService<IOutboxTenantDetector>();
        int[] tenants = await detector.GetTenantIds(ct);

        Assert.Contains(first, tenants);
        Assert.Contains(second, tenants);
    }

    [Fact]
    public async Task PublishWithContext_IsRejected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        IOutboxMessagePublisher publisher = Services.GetRequiredService<IOutboxMessagePublisher>();

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await publisher.Publish([new TestMessage("x")], NextTenant(), new object(), ct));
    }

    private RedisKey StreamKey(int tenant, OutboxMessageFilter filter)
        => Settings.KeySettings.StreamKey(tenant, filter.Part, filter.PayloadType);
}
