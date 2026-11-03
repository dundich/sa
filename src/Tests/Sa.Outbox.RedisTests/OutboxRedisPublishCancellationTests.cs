using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis.Configuration;
using StackExchange.Redis;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// Cancelling a publish has to mean "these messages are not stored". The Redis provider cannot
/// abort an XADD that is already in flight — StackExchange.Redis has no cancellation on its async
/// commands — but it must at least not start one for a token that is already cancelled, and must
/// not report success for a publish it never performed.
/// </summary>
public class OutboxRedisPublishCancellationTests(OutboxRedisFixture fixture)
    : IClassFixture<OutboxRedisFixture>
{
    private static int s_tenant = 3000;

    private static int NextTenant() => Interlocked.Increment(ref s_tenant);

    private IServiceProvider Services => fixture.ServiceProvider;

    private IDatabase Database => Services.GetRequiredService<IDatabase>();

    private IOutboxMessagePublisher Publisher => Services.GetRequiredService<IOutboxMessagePublisher>();

    private RedisOutboxSettings Settings => Services.GetRequiredService<RedisOutboxSettings>();

    private long Stored(int tenantId)
        => Database.StreamLength(
            Settings.KeySettings.StreamKey(tenantId, OutboxRedisFixture.PartName, nameof(TestMessage)));

    [Fact]
    public async Task Publish_CancelledToken_ThrowsAndStoresNothing()
    {
        int tenant = NextTenant();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Publisher.Publish([new TestMessage("r1"), new TestMessage("r2")], tenant, cts.Token).AsTask());

        Assert.Equal(0, Stored(tenant));
    }

    /// <summary>
    /// The single-message overload used to bypass the chunk loop's cancellation check and hand the
    /// token to a provider that cannot observe it — the entry was appended and the caller got a
    /// success back for a publish it had already cancelled.
    /// </summary>
    [Fact]
    public async Task PublishSingle_CancelledToken_ThrowsAndStoresNothing()
    {
        int tenant = NextTenant();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Publisher.PublishSingle(new TestMessage("r3"), tenant, cts.Token).AsTask());

        Assert.Equal(0, Stored(tenant));
    }
}
