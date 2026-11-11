using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

/// <summary>
/// Multi-provider group identity: the same group id may exist under two providers. The manager and
/// the consumers are keyed by <see cref="OutboxGroupKey"/>; the legacy string API fails loudly when
/// the id is ambiguous instead of picking a provider.
/// </summary>
public class MultiProviderGroupUniquenessTests
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

    [Fact]
    public void SameGroupId_UnderDifferentProviders_IsAddressableByCompositeKey()
    {
        var services = new ServiceCollection();
        services.AddSaOutbox(
            builder => builder.WithDeliveries(deliveries => deliveries
                .AddDelivery<TestConsumer, TestMessage>("shared_group")),
            "provider-a");
        services.AddSaOutbox(
            builder => builder.WithDeliveries(deliveries => deliveries
                .AddDelivery<TestConsumer, TestMessage>("shared_group")),
            "provider-b");

        using ServiceProvider sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IOutboxConsumerManager>();

        var a = manager.GetSettings(new OutboxGroupKey("provider-a", "shared_group"));
        var b = manager.GetSettings(new OutboxGroupKey("provider-b", "shared_group"));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("provider-a", a.ProviderKey);
        Assert.Equal("provider-b", b.ProviderKey);

        // The legacy string API cannot pick one provider and must not guess.
        Assert.Throws<InvalidOperationException>(() => manager.GetSettings("shared_group"));
    }

    [Fact]
    public void SameGroupId_UnderOneProvider_IsStillTolerated()
    {
        var services = new ServiceCollection();
        services.AddSaOutbox(
            builder => builder.WithDeliveries(deliveries => deliveries
                .AddDelivery<TestConsumer, TestMessage>("shared_group")
                .AddDelivery<TestConsumer, TestMessage>("shared_group")),
            "provider-a");

        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IOutboxConsumerManager>());
    }
}
