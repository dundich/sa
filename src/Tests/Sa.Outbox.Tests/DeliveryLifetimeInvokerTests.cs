using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

/// <summary>
/// Regression tests for the singleton-consumer cache of <see cref="DeliveryLifetimeInvoker"/>.
/// </summary>
public class DeliveryLifetimeInvokerTests
{
    private sealed class MsgA;

    private sealed class MsgB;

    private sealed class ConsumerA : IConsumer<MsgA>
    {
        public static int Created;

        public ConsumerA() => Created++;

        public int Calls;

        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<MsgA>> messages,
            CancellationToken cancellationToken)
        {
            Calls++;
            return default;
        }
    }

    private sealed class ConsumerB : IConsumer<MsgB>
    {
        public int Calls;
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<MsgB>> messages,
            CancellationToken cancellationToken)
        {
            Calls++;
            return default;
        }
    }

    private static OutboxConsumerSettings SettingsFor(string groupId) =>
        new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId(groupId)
            .AsSingleton()
            .Build();

    private static OutboxMessageFilter FilterFor(string groupId) =>
        new(
            TransactId: "test",
            ConsumerGroupId: groupId,
            PayloadType: "test",
            TenantId: 1,
            Part: "test",
            FromDate: DateTimeOffset.UnixEpoch,
            ToDate: DateTimeOffset.UnixEpoch,
            NowDate: DateTimeOffset.UnixEpoch);

    /// <summary>
    /// A consumer group id is only unique per message type, so two <c>TMessage</c> values may legally
    /// share one group. Each must get its own consumer instance — keying the cache by group alone
    /// handed the second message type the first one's instance and threw InvalidCastException
    /// inside the delivery path.
    /// </summary>
    [Fact]
    public async Task ConsumeInScope_SameGroup_DifferentMessageTypes_EachGetOwnConsumer()
    {
        const string group = "shared_group";

        var consumerA = new ConsumerA();
        var consumerB = new ConsumerB();

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConsumer<MsgA>>(group, consumerA);
        services.AddKeyedSingleton<IConsumer<MsgB>>(group, consumerB);

        await using var provider = services.BuildServiceProvider();

        var invoker = new DeliveryLifetimeInvoker(provider);
        var settings = SettingsFor(group);
        var filter = FilterFor(group);

        await invoker.ConsumeInScope<MsgA>(
            settings, filter, ReadOnlyMemory<IOutboxContextOperations<MsgA>>.Empty, TestContext.Current.CancellationToken);

        await invoker.ConsumeInScope<MsgB>(
            settings, filter, ReadOnlyMemory<IOutboxContextOperations<MsgB>>.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(1, consumerA.Calls);
        Assert.Equal(1, consumerB.Calls);
    }

    /// <summary>
    /// The cached instance must be reused across calls for the same (message type, group) pair.
    /// </summary>
    [Fact]
    public async Task ConsumeInScope_SameMessageTypeAndGroup_ReusesCachedConsumer()
    {
        const string group = "shared_group";

        var consumer = new ConsumerA();

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConsumer<MsgA>>(group, consumer);

        await using var provider = services.BuildServiceProvider();

        var invoker = new DeliveryLifetimeInvoker(provider);
        var settings = SettingsFor(group);
        var filter = FilterFor(group);

        await invoker.ConsumeInScope<MsgA>(
            settings, filter, ReadOnlyMemory<IOutboxContextOperations<MsgA>>.Empty, TestContext.Current.CancellationToken);

        await invoker.ConsumeInScope<MsgA>(
            settings, filter, ReadOnlyMemory<IOutboxContextOperations<MsgA>>.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(2, consumer.Calls);
    }

    /// <summary>
    /// With <c>AsSingleton = false</c> a fresh DI scope is created per batch, so the cache must not
    /// be consulted at all — a new consumer is constructed for every batch.
    /// </summary>
    [Fact]
    public async Task ConsumeInScope_Scoped_CreatesNewConsumerPerBatch()
    {
        const string group = "scoped_group";

        ConsumerA.Created = 0;

        var services = new ServiceCollection();
        services.AddKeyedScoped<IConsumer<MsgA>, ConsumerA>(group);

        await using var provider = services.BuildServiceProvider();

        var invoker = new DeliveryLifetimeInvoker(provider);
        var settings = SettingsFor(group) with { AsSingleton = false };
        var filter = FilterFor(group);

        await invoker.ConsumeInScope<MsgA>(
            settings, filter, ReadOnlyMemory<IOutboxContextOperations<MsgA>>.Empty, TestContext.Current.CancellationToken);

        await invoker.ConsumeInScope<MsgA>(
            settings, filter, ReadOnlyMemory<IOutboxContextOperations<MsgA>>.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(2, ConsumerA.Created);
    }
}
