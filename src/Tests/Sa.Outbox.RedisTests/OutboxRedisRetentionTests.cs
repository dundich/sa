using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis.Configuration;
using StackExchange.Redis;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// Retention tests use the same container shape as the provider tests but get their own Redis
/// instance, because they enable trimming globally for the fixture.
/// </summary>
public class OutboxRedisRetentionTests(OutboxRedisFixture fixture) : IClassFixture<OutboxRedisFixture>
{
    private static int s_tenant = 2000;

    private static int NextTenant() => Interlocked.Increment(ref s_tenant);

    private IServiceProvider Services => fixture.ServiceProvider;

    private IDatabase Database => Services.GetRequiredService<IDatabase>();

    private IOutboxDeliveryManager Manager => Services.GetRequiredService<IOutboxDeliveryManager>();

    private IOutboxMessagePublisher Publisher => Services.GetRequiredService<IOutboxMessagePublisher>();

    private RedisOutboxSettings Settings => Services.GetRequiredService<RedisOutboxSettings>();

    private RedisOutboxRetentionSettings Retention => Settings.RetentionSettings;

    [Fact]
    public async Task StreamTrim_KeepsPendingEntriesAndRemovesAcknowledgedOnes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        EnableRetention(streamMaxAge: TimeSpan.Zero);

        try
        {
            RedisKey stream = StreamKey(tenant);
            await PublishAsync(tenant, ct, "a", "b", "c");

            OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "retention-group");
            IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[8];

            // All three entries are pending now: the oldest pending id is the safe floor, so the
            // publish below must not remove anything.
            Assert.Equal(3, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct));

            await Task.Delay(20, ct);
            await PublishAsync(tenant, ct, "d");

            Assert.Equal(4, await Database.StreamLengthAsync(stream));

            foreach (IOutboxContextOperations<TestMessage> context in buffer.Take(3))
            {
                context.Ok();
            }

            Assert.Equal(3, await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, 3), filter, ct));

            // No pending entries remain, so the group cursor (c) is the floor: everything before it
            // is delivered and acknowledged and can be dropped.
            await Task.Delay(20, ct);
            await PublishAsync(tenant, ct, "e");

            Assert.Equal(3, await Database.StreamLengthAsync(stream));

            StreamEntry[] remaining = await Database.StreamRangeAsync(stream);
            Assert.Equal(["c", "d", "e"], remaining.Select(e => (string?)e["i"]));
        }
        finally
        {
            DisableRetention();
        }
    }

    [Fact]
    public async Task RetentionDisabled_NeverTrims()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        Assert.False(Retention.Enabled);

        await PublishAsync(tenant, ct, "a", "b", "c");

        OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "disabled-group");
        IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[8];

        Assert.Equal(3, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct));

        foreach (IOutboxContextOperations<TestMessage> context in buffer.Take(3))
        {
            context.Ok();
        }

        await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, 3), filter, ct);

        // Acknowledged entries stay in the stream when retention is off; the publish only appends.
        await PublishAsync(tenant, ct, "d");

        Assert.Equal(4, await Database.StreamLengthAsync(StreamKey(tenant)));
    }

    private Task PublishAsync(int tenant, CancellationToken ct, params string[] ids)
        => Publisher.Publish(ids.Select(id => new TestMessage(id)).ToArray(), tenant, ct).AsTask();

    private RedisKey StreamKey(int tenant)
        => Settings.KeySettings.StreamKey(tenant, OutboxRedisFixture.PartName, typeof(TestMessage).Name);

    private void EnableRetention(TimeSpan streamMaxAge)
    {
        Retention.Enabled = true;
        Retention.StreamMaxAge = streamMaxAge;
        Retention.TrimInterval = TimeSpan.FromMilliseconds(1);
        // Exact trimming makes the assertions deterministic; the approximate mode would be allowed
        // to leave entries behind.
        Retention.Approximate = false;
        Retention.TrimLimit = 0;
    }

    private void DisableRetention()
    {
        Retention.Enabled = false;
        Retention.StreamMaxAge = RedisOutboxRetentionSettings.Defaults.StreamMaxAge;
        Retention.TrimInterval = RedisOutboxRetentionSettings.Defaults.TrimInterval;
        Retention.Approximate = true;
        Retention.TrimLimit = RedisOutboxRetentionSettings.Defaults.TrimLimit;
    }
}
