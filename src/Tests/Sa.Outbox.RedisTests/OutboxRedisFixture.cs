using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.Redis;
using Testcontainers.Redis;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// One Redis container per test class. Redis is cheap to start, and a container per class keeps the
/// streams of unrelated test classes apart without key prefixes.
/// </summary>
public class OutboxRedisFixture : SaFixture
{
    /// <summary>The part name every test message is published under.</summary>
    public const string PartName = "test";

    private readonly RedisContainer _container = new RedisBuilder("redis:7.4-alpine").Build();

    public OutboxRedisFixture()
    {
        Services
            .AddSaOutboxUsingRedis(cfg =>
            {
                // The container is started in InitializeAsync, so the connection string is read
                // lazily, on the first Redis command.
                cfg.WithConnectionString(_ => _container.GetConnectionString());
            })
            .AddSaOutbox(builder => builder
                .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>(PartName, message => message.PayloadId)));
    }

    public string ConnectionString => _container.GetConnectionString();

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

    /// <summary>Builds the filter the delivery pipeline would build for one poll of a group.</summary>
    public static OutboxMessageFilter CreateFilter<TMessage>(
        int tenantId,
        string consumerGroupId,
        DateTimeOffset? now = null,
        TimeSpan? batchingWindow = null)
    {
        DateTimeOffset moment = now ?? DateTimeOffset.UtcNow;

        return new OutboxMessageFilter
        {
            TransactId = Guid.CreateVersion7().ToString("N"),
            ConsumerGroupId = consumerGroupId,
            PayloadType = typeof(TMessage).Name,
            TenantId = tenantId,
            Part = PartName,
            FromDate = moment.AddDays(-1),
            ToDate = moment - (batchingWindow ?? TimeSpan.Zero),
            NowDate = moment
        };
    }
}
