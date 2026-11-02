using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis;
using StackExchange.Redis;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// The default provider registers an unkeyed <see cref="IDatabase"/> alias for backward
/// compatibility. A provider registered later under its own key must not mistake it for an
/// application-provided database — with that bug its connection string was ignored and its messages
/// were written into the default provider's database.
/// </summary>
public class DefaultAndKeyedConnectionTests(OutboxRedisFixture fixture) : IClassFixture<OutboxRedisFixture>
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KeyedProvider_WritesToItsOwnDatabase()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services
            .AddSaOutboxUsingRedis(
                cfg => cfg.WithConnectionString(fixture.ConnectionString),
                outbox => outbox
                    .WithTenants((_, tenants) => tenants.WithTenantIds(1))
                    .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>("part", m => m.PayloadId)))
            .AddSaOutboxUsingRedis(
                cfg => cfg.WithConnectionString(fixture.ConnectionString).WithDatabaseIndex(1),
                outbox => outbox
                    .WithTenants((_, tenants) => tenants.WithTenantIds(2))
                    .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>("part", m => m.PayloadId)),
                "keyed-db");

        using ServiceProvider sp = services.BuildServiceProvider();

        IOutboxPublisherResolver resolver = sp.GetRequiredService<IOutboxPublisherResolver>();
        await resolver.For("keyed-db").PublishSingle(new TestMessage("keyed-1"), tenantId: 2, TestToken);

        using var inspector = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);

        // The keyed provider owns its connection: database 1 has its stream, database 0 (the
        // default provider's) stayed empty. Before the fix the alias sent the write to database 0.
        long defaultDatabaseKeys = (long)await inspector.GetDatabase(0).ExecuteAsync("DBSIZE");
        long keyedDatabaseKeys = (long)await inspector.GetDatabase(1).ExecuteAsync("DBSIZE");

        Assert.Equal(0L, defaultDatabaseKeys);
        Assert.True(keyedDatabaseKeys > 0);
    }
}
