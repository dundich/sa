using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests.PlugServices;

/// <summary>
/// Scope-key consistency: every consumer group names the provider key its storage lives under, so a
/// group registered under a scope with no provider is a configuration error — its delivery would
/// resolve no storage at all. The startup validator catches it instead of failing on the first tick.
/// </summary>
public class ScopeKeyConsistencyTests
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

    internal sealed class FakeWriter : IOutboxBulkWriter
    {
        public ValueTask<ulong> InsertBulk<TMessage>(
            OutboxMessageBatch<TMessage> batch,
            object? context,
            CancellationToken cancellationToken = default)
            => new((ulong)batch.Count);
    }

    internal sealed class FakeDeliveryManager : IOutboxDeliveryManager
    {
        public Task<int> RentDelivery<TMessage>(
            Memory<IOutboxContextOperations<TMessage>> writeBuffer,
            TimeSpan lockDuration,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<int> ReturnDelivery<TMessage>(
            ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
            => Task.FromResult(messages.Length);

        public Task<int> ExtendDelivery(
            TimeSpan lockExpiration,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
            => Task.FromResult(0);
    }

    private static ServiceProvider Build(string providerKey, string? groupScopeKey)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddOutboxProvider(
            providerKey,
            new OutboxProviderDescriptor("Test"),
            provider => provider
                .WithBulkWriter<FakeWriter>()
                .WithDeliveryManager<FakeDeliveryManager>());

        if (groupScopeKey is not null)
        {
            services.AddSaOutbox(
                builder => builder.WithDeliveries(deliveries => deliveries
                    .AddDelivery<TestConsumer, TestMessage>("grp")),
                groupScopeKey);
        }

        return services.BuildServiceProvider();
    }

    private static OutboxProviderValidationHostedService Validator(ServiceProvider sp)
        => Assert.Single(sp.GetServices<IHostedService>().OfType<OutboxProviderValidationHostedService>());

    [Fact]
    public async Task GroupUnderAScopeWithoutProvider_FailsTheStart()
    {
        using ServiceProvider sp = Build(providerKey: "a", groupScopeKey: "other");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validator(sp).StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("other", error.Message, StringComparison.Ordinal);
        Assert.Contains("no storage provider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultScopeGroup_WithOnlyANamedProvider_Fails()
    {
        // The classic mistake: AddSaOutbox() without a key and the provider registered under a
        // custom key. The group's provider key stays the default one, which has no provider.
        using ServiceProvider sp = Build(providerKey: "a", groupScopeKey: OutboxScope.DefaultKey);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validator(sp).StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(OutboxScope.DefaultKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchingScopeAndProvider_Passes()
    {
        using ServiceProvider sp = Build(providerKey: "a", groupScopeKey: "a");

        await Validator(sp).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NoConsumerGroups_Passes()
    {
        using ServiceProvider sp = Build(providerKey: "a", groupScopeKey: null);

        await Validator(sp).StartAsync(TestContext.Current.CancellationToken);
    }
}
