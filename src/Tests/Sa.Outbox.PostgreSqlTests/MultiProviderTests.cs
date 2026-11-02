using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests;

/// <summary>
/// Keyed multi-provider: two PostgreSQL outboxes in one container, each with its own schema,
/// provider key, publisher and consumer group. Proves the slice-1 shape — the provider key is the
/// scope key, storage contracts are keyed, and the publisher facade addresses each provider.
/// </summary>
public class MultiProviderTests(MultiProviderTests.Fixture fixture)
    : IClassFixture<MultiProviderTests.Fixture>
{
    private const string ProviderA = "mp-a";
    private const string ProviderB = "mp-b";

    private const string SchemaA = "mp_a";
    private const string SchemaB = "mp_b";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public sealed class TestMessage
    {
        public string PayloadId { get; set; } = string.Empty;
        public int TenantId { get; set; } = 1;
    }

    internal sealed class ConsumerA : IConsumer<TestMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    internal sealed class ConsumerB : IConsumer<TestMessage>
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
        public Fixture()
        {
            Services
                .AddSaOutboxUsingPostgreSql(
                    ConfigureProvider(SchemaA),
                    outbox => outbox
                        .WithTenants((_, tenants) => tenants.WithTenantIds(1))
                        .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>("mp", m => m.PayloadId))
                        .WithDeliveries(deliveries => deliveries
                            .AddDelivery<ConsumerA, TestMessage>("mp_a_group")),
                    ProviderA)
                .AddSaOutboxUsingPostgreSql(
                    ConfigureProvider(SchemaB),
                    outbox => outbox.WithDeliveries(deliveries => deliveries
                        .AddDelivery<ConsumerB, TestMessage>("mp_b_group")),
                    ProviderB);
        }

        private Action<IPgOutboxConfiguration> ConfigureProvider(string schema)
            => pg => pg
                .WithDataSource(b => b.Options(options => options.Configure(
                    o => o.ConnectionString = ConnectionString)))
                .WithMessageSerializer(_ => OutboxMessageSerializer.Instance)
                .WithOutboxSettings((_, settings) => settings.TableSettings.WithSchema(schema));
    }

    [Fact]
    public async Task Publish_ToEachProvider_WritesToItsOwnSchema()
    {
        IOutboxPublisherResolver resolver = fixture.ServiceProvider.GetRequiredService<IOutboxPublisherResolver>();

        Assert.Equal(1UL, await resolver.For(ProviderA).PublishSingle(
            new TestMessage { PayloadId = "a-1" }, tenantId: 1, TestToken));
        Assert.Equal(1UL, await resolver.For(ProviderB).PublishSingle(
            new TestMessage { PayloadId = "b-1" }, tenantId: 1, TestToken));

        Assert.Equal(1L, await CountMessages(SchemaA, "a-1"));
        Assert.Equal(0L, await CountMessages(SchemaA, "b-1"));
        Assert.Equal(1L, await CountMessages(SchemaB, "b-1"));
        Assert.Equal(0L, await CountMessages(SchemaB, "a-1"));
    }

    [Fact]
    public void EachProvider_HasItsOwnKeyedDescriptorAndPublisher()
    {
        IServiceProvider sp = fixture.ServiceProvider;
        var resolver = sp.GetRequiredService<IOutboxPublisherResolver>();

        Assert.NotSame(resolver.For(ProviderA), resolver.For(ProviderB));

        IOutboxProviderRegistry registryA =
            sp.GetRequiredKeyedService<IOutboxProviderRegistry>(ProviderA);
        IOutboxProviderRegistry registryB =
            sp.GetRequiredKeyedService<IOutboxProviderRegistry>(ProviderB);

        Assert.Equal("PostgreSql", registryA.Descriptor.Name);
        Assert.Equal("PostgreSql", registryB.Descriptor.Name);
        Assert.NotSame(registryA, registryB);
    }

    [Fact]
    public void UnknownProviderKey_FailsWithANamedError()
    {
        var resolver = fixture.ServiceProvider.GetRequiredService<IOutboxPublisherResolver>();

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => resolver.For("nope"));

        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_ReportsEveryProvider()
    {
        IReadOnlyList<OutboxProviderHealthReport> reports =
            await fixture.ServiceProvider.GetRequiredService<IOutboxHealth>().CheckAsync(TestToken);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, report => Assert.True(report.Healthy, report.Reason));
        Assert.Equal(
            new[] { ProviderA, ProviderB },
            reports.Select(report => report.ProviderKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ConsumerGroups_AreBoundToTheirProviderKey()
    {
        var manager = fixture.ServiceProvider.GetRequiredService<IOutboxConsumerManager>();

        Assert.Equal(ProviderA, manager.Get("mp_a_group")?.ProviderKey);
        Assert.Equal(ProviderB, manager.Get("mp_b_group")?.ProviderKey);
    }

    private async Task<long> CountMessages(string schema, string payloadId)
        => await fixture.DataSource.ExecuteScalarTyped<long>(
            $"SELECT count(*) FROM {schema}.outbox__msg$ WHERE msg_payload_id = '{payloadId}'",
            initCommand: null,
            TestToken);
}
