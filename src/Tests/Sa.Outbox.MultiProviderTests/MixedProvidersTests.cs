using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Serialization;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Sa.Outbox.MultiProviderTests;

/// <summary>
/// Two different providers in one container: PostgreSQL under one key and Redis under another.
/// Proves the cross-provider case that the per-provider suites cannot: every keyed service is
/// addressed by its own key, publishing lands in the right storage, and health covers both.
/// </summary>
public class MixedProvidersTests(MixedProvidersTests.Fixture fixture)
    : IClassFixture<MixedProvidersTests.Fixture>
{
    internal const string PgKey = "mixed-pg";
    internal const string RedisKey = "mixed-redis";
    internal const string Part = "mixed";

    internal sealed class TestMessage
    {
        public TestMessage() { }

        public TestMessage(string payloadId) => PayloadId = payloadId;

        public string PayloadId { get; set; } = string.Empty;
        public int TenantId { get; set; }
    }

    /// <summary>Wraps JSON unchanged, so the storage behaviour is identical; the type is the marker.</summary>
    internal sealed class WrappingSerializer(IOutboxMessageSerializer inner) : IOutboxMessageSerializer
    {
        public T? Deserialize<T>(Stream stream) => inner.Deserialize<T>(stream);

        public void Serialize<T>(Stream stream, T value) => inner.Serialize(stream, value);
    }

    internal static readonly IOutboxMessageSerializer PgSerializer =
        new WrappingSerializer(JsonOutboxMessageSerializer.Instance);

    internal static readonly IOutboxMessageSerializer RedisSerializer =
        new WrappingSerializer(JsonOutboxMessageSerializer.Instance);

    internal sealed class PgConsumer : IConsumer<TestMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    internal sealed class RedisConsumer : IConsumer<TestMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    public sealed class Fixture : PgDataSourceFixture
    {
        private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();

        public Fixture()
        {
            Services
                .AddSaOutboxUsingPostgreSql(
                    ConfigurePostgres,
                    outbox => outbox
                        .WithTenants((_, tenants) => tenants.WithTenantIds(1))
                        .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>(Part, m => m.PayloadId))
                        .WithDeliveries(deliveries => deliveries
                            .AddDelivery<PgConsumer, TestMessage>("mixed_pg_group")),
                    PgKey)
                .AddSaOutboxUsingRedis(
                    ConfigureRedis,
                    outbox => outbox
                        .WithTenants((_, tenants) => tenants.WithTenantIds(2))
                        .WithDeliveries(deliveries => deliveries
                            .AddDelivery<RedisConsumer, TestMessage>("mixed_redis_group")),
                    RedisKey);
        }

        private void ConfigurePostgres(IPgOutboxConfiguration pg)
            => pg
                .WithDataSource(ds => ds.Options(options => options.Configure(
                    o => o.ConnectionString = ConnectionString)))
                .WithOutboxSettings((_, settings) => settings.TableSettings.WithSchema("mixed_pg"))
                .WithMessageSerializer(PgSerializer);

        private void ConfigureRedis(IRedisOutboxConfiguration redis)
            => redis
                // The container starts in InitializeAsync; the string is read on first command.
                .WithConnectionString(_ => _redis.GetConnectionString())
                // An explicit prefix: the provider key suffix would also work, but the test pins
                // the exact key it reads the stream from.
                .WithOutboxSettings((_, settings) => settings.KeySettings.Prefix = "mp_mixed")
                .WithMessageSerializer(RedisSerializer);

        public override async ValueTask InitializeAsync()
        {
            await _redis.StartAsync();
            await base.InitializeAsync();
        }

        public override async ValueTask DisposeAsync()
        {
            await _redis.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publish_ToBothProviders_LandsInItsOwnStorage()
    {
        IServiceProvider sp = fixture.ServiceProvider;
        IOutboxPublisherResolver resolver = sp.GetRequiredService<IOutboxPublisherResolver>();

        Assert.NotSame(resolver.GetPublisher(PgKey), resolver.GetPublisher(RedisKey));

        // Distinct tenants keep the two provider storages independent within the shared container.
        Assert.Equal(1UL, await resolver.GetPublisher(PgKey).PublishSingle(new TestMessage("pg-1"), tenantId: 11, TestToken));
        Assert.Equal(1UL, await resolver.GetPublisher(RedisKey).PublishSingle(new TestMessage("redis-1"), tenantId: 12, TestToken));

        Assert.Equal(1L, await CountPostgresMessages("pg-1"));
        Assert.Equal(0L, await CountPostgresMessages("redis-1"));

        Assert.Equal(1L, await RedisStreamLength(tenantId: 12));
        // Nothing was written to the PostgreSQL provider's tenant stream namespace.
        Assert.Equal(0L, await RedisStreamLength(tenantId: 11));
    }

    [Fact]
    public async Task Health_ReportsBothProvidersHealthy()
    {
        IReadOnlyList<OutboxProviderHealthReport> reports =
            await fixture.ServiceProvider.GetRequiredService<IOutboxHealth>().CheckAsync(TestToken);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, report => Assert.True(report.Healthy, report.Reason));
        Assert.Equal(
            new[] { PgKey, RedisKey },
            reports.Select(report => report.ProviderKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WarmUp_RunsForBothProviders()
    {
        IServiceProvider sp = fixture.ServiceProvider;

        // The hook takes no token: the warm-up is shared, and its wait is what observes a caller's
        // cancellation.
        await sp.GetRequiredKeyedService<IOutboxProviderInitializer>(PgKey).InitializeAsync();
        await sp.GetRequiredKeyedService<IOutboxProviderInitializer>(RedisKey).InitializeAsync();
    }

    [Fact]
    public void Serializer_IsPerProvider()
    {
        IServiceProvider sp = fixture.ServiceProvider;

        // Each provider resolves its own codec; the second registration no longer overwrites the first.
        Assert.Same(PgSerializer, sp.GetRequiredKeyedService<IOutboxMessageSerializer>(PgKey));
        Assert.Same(RedisSerializer, sp.GetRequiredKeyedService<IOutboxMessageSerializer>(RedisKey));
    }

    [Fact]
    public async Task EachProvider_HasItsOwnTenants()
    {
        IServiceProvider sp = fixture.ServiceProvider;

        var pg = sp.GetRequiredKeyedService<Sa.Outbox.Partitional.TenantSettings>(PgKey);
        var redis = sp.GetRequiredKeyedService<Sa.Outbox.Partitional.TenantSettings>(RedisKey);

        Assert.Equal(new[] { 1 }, await pg.GetTenantIds(TestToken));
        Assert.Equal(new[] { 2 }, await redis.GetTenantIds(TestToken));
    }

    [Fact]
    public void ConsumerGroups_AreBoundToTheirProvider()
    {
        var manager = fixture.ServiceProvider.GetRequiredService<IOutboxConsumerManager>();

        Assert.Equal(PgKey, manager.GetSettings(new OutboxGroupKey(PgKey, "mixed_pg_group"))?.ProviderKey);
        Assert.Equal(RedisKey, manager.GetSettings(new OutboxGroupKey(RedisKey, "mixed_redis_group"))?.ProviderKey);
    }

    private async Task<long> CountPostgresMessages(string payloadId)
    {
        const string sql = """
            SELECT count(*) FROM mixed_pg.outbox__msg$
            WHERE msg_payload_id = @id
            """;

        return await fixture.DataSource.ExecuteScalarTypedAsync<long>(
            sql,
            [new NpgsqlParameter("id", payloadId)],
            TestToken);
    }

    private async Task<long> RedisStreamLength(int tenantId)
    {
        IServiceProvider sp = fixture.ServiceProvider;

        RedisOutboxSettings settings = sp.GetRequiredKeyedService<RedisOutboxSettings>(RedisKey);
        IDatabase database = sp.GetRequiredKeyedService<IDatabase>(RedisKey);

        string streamKey = settings.KeySettings.StreamKey(
            tenantId,
            Part,
            typeof(TestMessage).Name);

        return await database.StreamLengthAsync(streamKey);
    }
}
