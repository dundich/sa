using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis;
using Sa.Outbox.Redis.Configuration;
using Testcontainers.Redis;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// The Redis provider counts a delivery when the entry is handed to a consumer, not when the
/// outcome is written — there is nothing else to count, the pending entries list is the only state.
/// So a cancelled pass neither loses the message (it stays in the PEL and is reclaimed) nor keeps
/// its retry budget intact: the rent itself already spent one attempt.
/// <para>
/// This is the deliberate provider asymmetry against PostgreSQL, where the attempt counter is
/// incremented by the finish statement and a cancelled batch costs nothing.
/// </para>
/// </summary>
public class OutboxRedisCancelledRentTests(OutboxRedisCancelledRentTests.Fixture fixture)
    : IClassFixture<OutboxRedisCancelledRentTests.Fixture>
{
    private const string Group = "cancel-retry";
    private const int TenantId = 4242;

    public class Fixture : SaFixture
    {
        /// <summary>Long enough to be meaningful, short enough to wait for in a test.</summary>
        public static readonly TimeSpan PendingClaimIdle = TimeSpan.FromMilliseconds(200);

        private readonly RedisContainer _container = new RedisBuilder("redis:7.4-alpine").Build();

        public Fixture()
        {
            Services
                .AddSaOutboxUsingRedis(cfg =>
                {
                    cfg.WithConnectionString(_ => _container.GetConnectionString());
                    cfg.WithOutboxSettings((_, settings) =>
                        settings.ConsumeSettings.PendingClaimIdle = PendingClaimIdle);
                })
                .AddSaOutbox(builder => builder
                    .WithMetadata((_, metadata) =>
                        metadata.AddMetadata<TestMessage>("test", message => message.PayloadId)));
        }

        public override async ValueTask InitializeAsync()
        {
            await _container.StartAsync();
            await base.InitializeAsync();
        }

        public override async ValueTask DisposeAsync()
        {
            await _container.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private IOutboxDeliveryManager Manager =>
        fixture.ServiceProvider.GetRequiredService<IOutboxDeliveryManager>();

    private IOutboxMessagePublisher Publisher =>
        fixture.ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

    [Fact]
    public async Task CancelledRent_LosesNothing_ButSpendsOneAttempt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        ulong published = await Publisher.Publish(
            [new TestMessage("cancelled-1")], TenantId, ct);
        Assert.Equal(1UL, published);

        IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

        // The pass that will be "cancelled": it rents and never returns a status.
        OutboxMessageFilter first = OutboxRedisFixture
            .CreateFilter<TestMessage>(TenantId, Group)
            with
        { TransactId = "pass-1" };

        int rentedByCancelled = await Manager.RentDelivery<TestMessage>(
            buffer, TimeSpan.FromMinutes(1), first, ct);

        Assert.Equal(1, rentedByCancelled);
        Assert.Equal(0, buffer[0].DeliveryInfo.Attempt);

        // A cancellation leaves the entry in the pending entries list; it becomes claimable again
        // once it has been idle for PendingClaimIdle.
        await Task.Delay(Fixture.PendingClaimIdle * 3, ct);

        OutboxMessageFilter second = first with
        {
            TransactId = "pass-2",
            NowDate = DateTimeOffset.UtcNow,
        };

        int rentedAgain = await Manager.RentDelivery<TestMessage>(
            buffer, TimeSpan.FromMinutes(1), second, ct);

        // Not lost: the same message comes back...
        Assert.Equal(1, rentedAgain);
        Assert.Equal("cancelled-1", buffer[0].Payload.PayloadId);

        // ...but its retry budget was already spent by the rent, not by an outcome.
        Assert.Equal(1, buffer[0].DeliveryInfo.Attempt);

        buffer[0].Ok();
        await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, rentedAgain), second, ct);
    }
}
