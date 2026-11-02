using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

/// <summary>
/// Slice-1 multi-provider contract: consumer group ids are unique across providers because the
/// consumer manager and <see cref="IConsumer{TMessage}"/> are keyed by the group id alone.
/// </summary>
public class MultiProviderGroupUniquenessTests
{
    internal sealed class TestMessage
    {
        public int TenantId { get; set; }
    }

    internal sealed class TestConsumer : IConsumer<TestMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public void SameGroupId_UnderDifferentProviders_ThrowsOnManagerResolution()
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

        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<IOutboxConsumerManager>());
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
