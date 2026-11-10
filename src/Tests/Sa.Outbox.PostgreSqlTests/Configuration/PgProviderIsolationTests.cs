using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Two providers in one container must stay isolated even when a consumer group id repeats: the
/// per-provider snapshot exposes only its own groups, and an unknown non-default key never resolves
/// the default provider's storage.
/// </summary>
public class PgProviderIsolationTests
{
    public sealed class Message
    {
        public string PayloadId { get; set; } = string.Empty;
    }

    private sealed class ConsumerA : IConsumer<Message>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<Message>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class ConsumerB : IConsumer<Message>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<Message>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static void ConfigureProvider(IPgOutboxConfiguration pg)
        => pg.WithDataSource(ds => ds.Options(options => options.Configure(
            o => o.ConnectionString = "Host=localhost;Database=outbox")));

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // A connection string only satisfies the data-source options validation; no server is
        // contacted because the data source opens connections lazily.
        services
            .AddSaOutboxUsingPostgreSql(
                ConfigureProvider,
                outbox => outbox
                    .WithMetadata((_, metadata) => metadata.AddMetadata<Message>("part", m => m.PayloadId))
                    .WithDeliveries(deliveries => deliveries.AddDelivery<ConsumerA, Message>("shared")),
                "snap-a")
            .AddSaOutboxUsingPostgreSql(
                ConfigureProvider,
                outbox => outbox
                    .WithMetadata((_, metadata) => metadata.AddMetadata<Message>("part", m => m.PayloadId))
                    .WithDeliveries(deliveries => deliveries.AddDelivery<ConsumerB, Message>("shared")),
                "snap-b");

        return services;
    }

    [Fact]
    public void DeliverySnapshot_ContainsOnlyItsOwnProviderGroups_EvenWithTheSameGroupId()
    {
        using ServiceProvider provider = BuildServices().BuildServiceProvider();

        IDeliverySnapshot a = provider.GetRequiredKeyedService<IDeliverySnapshot>("snap-a");
        IDeliverySnapshot b = provider.GetRequiredKeyedService<IDeliverySnapshot>("snap-b");

        // Before the fix the snapshot enumerated every provider's group id and the string lookup
        // threw on the duplicate; the same list also leaked other providers' groups into the
        // partition job.
        Assert.Equal(new[] { "shared" }, a.GetConsumeGroupIds());
        Assert.Equal(new[] { "shared" }, b.GetConsumeGroupIds());

        Assert.Equal("snap-a", a.ConsumerSettings.Single().ProviderKey);
        Assert.Equal("snap-b", b.ConsumerSettings.Single().ProviderKey);
    }

    [Fact]
    public void UnknownNonDefaultKey_DoesNotFallBackToTheDefaultProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSaOutboxUsingPostgreSql(cfg => cfg.WithDataSource());
        services.AddSaOutbox(scopeKey: "ghost");

        using ServiceProvider provider = services.BuildServiceProvider();

        IOutboxPublisherResolver resolver = provider.GetRequiredService<IOutboxPublisherResolver>();

        // Without the keyed registration the publisher used to resolve the default provider's writer
        // and silently publish into the wrong storage.
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => resolver.GetPublisher("ghost"));
        Assert.Contains("ghost", exception.Message);
    }
}
