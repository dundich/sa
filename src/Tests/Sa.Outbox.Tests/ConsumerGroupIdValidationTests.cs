using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

/// <summary>
/// A consumer group id is an identity, not a label: it is the consumer's DI key, the settings key,
/// the job name and part of the PostgreSQL partition name. It is therefore used verbatim, and an id
/// that would have to be rewritten is rejected instead of being silently collapsed into another one
/// (<c>es-group</c> / <c>ES_GROUP</c> / <c>es_group</c> must not become one group).
/// </summary>
public class ConsumerGroupIdValidationTests
{
    internal sealed class TestMessage
    {
        public int TenantId { get; set; }
    }

    internal sealed class TestConsumer : IConsumer<TestMessage>
    {
        public ValueTask ConsumeAsync(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("es-group")]
    [InlineData("ES_GROUP")]
    [InlineData("es group")]
    [InlineData("es.group")]
    [InlineData("es:group")]
    [InlineData("es_group-")]
    public void AddDelivery_WithNonCanonicalGroupId_Throws(string groupId)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Register(groupId, scoped: false));

        Assert.Contains(groupId, error.Message, StringComparison.Ordinal);
        Assert.Contains("lowercase ASCII", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es-group")]
    [InlineData("ES_GROUP")]
    public void AddDeliveryScoped_WithNonCanonicalGroupId_Throws(string groupId)
    {
        Assert.Throws<ArgumentException>(() => Register(groupId, scoped: true));
    }

    [Theory]
    [InlineData("es_group")]
    [InlineData("es_group_2")]
    [InlineData("cg_1234567890")]
    public void AddDelivery_WithCanonicalGroupId_RegistersItVerbatim(string groupId)
    {
        var services = new ServiceCollection();
        services.AddSaOutbox(builder => builder.WithDeliveries(deliveries =>
            deliveries.AddDelivery<TestConsumer, TestMessage>(groupId)));

        using ServiceProvider sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IOutboxConsumerManager>();

        Assert.True(manager.IsRegistered(groupId));
        Assert.NotNull(manager.GetSettings(groupId));
    }

    [Theory]
    [InlineData("Orders-V2")]
    [InlineData("ORDERS")]
    public void DefaultNamingStrategy_WithNonCanonicalName_ThrowsNamingTheStrategy(string name)
    {
        var services = new ServiceCollection();

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            services.AddSaOutbox(builder => builder.WithDeliveries(deliveries => deliveries
                .ConfigureDefaultNamingStrategy(new FixedNamingStrategy(name))
                .AddDelivery<TestConsumer, TestMessage>())));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.Contains("IConsumerGroupNamingStrategy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultNamingStrategy_WithCanonicalName_RegistersItVerbatim()
    {
        const string groupId = "cg_custom_name";
        var services = new ServiceCollection();

        services.AddSaOutbox(builder => builder.WithDeliveries(deliveries => deliveries
            .ConfigureDefaultNamingStrategy(new FixedNamingStrategy(groupId))
            .AddDelivery<TestConsumer, TestMessage>()));

        using ServiceProvider sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IOutboxConsumerManager>();

        Assert.True(manager.IsRegistered(groupId));
        Assert.NotNull(manager.GetSettings(groupId));
    }

    private static void Register(string groupId, bool scoped)
    {
        var services = new ServiceCollection();
        services.AddSaOutbox(builder => builder.WithDeliveries(deliveries =>
        {
            if (scoped)
                deliveries.AddDeliveryScoped<TestConsumer, TestMessage>(groupId);
            else
                deliveries.AddDelivery<TestConsumer, TestMessage>(groupId);
        }));
    }

    private sealed class FixedNamingStrategy(string name) : IConsumerGroupNamingStrategy
    {
        public string GetConsumerGroupName<TConsumer>() => name;
    }
}
