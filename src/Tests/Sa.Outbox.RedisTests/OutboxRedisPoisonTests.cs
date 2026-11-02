using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Poison;
using StackExchange.Redis;

namespace Sa.Outbox.RedisTests;

/// <summary>Records what the provider hands to the poison handler so the tests can assert on it.</summary>
public sealed class RecordingPoisonHandler : IRedisOutboxPoisonHandler
{
    private readonly ConcurrentQueue<RedisOutboxPoisonMessage> _messages = new();

    /// <summary>When set, <see cref="Handle"/> throws so the provider must leave the entry pending.</summary>
    public bool Throw { get; set; }

    public RedisOutboxPoisonMessage[] Messages => [.. _messages];

    public void Reset()
    {
        _messages.Clear();
        Throw = false;
    }

    public ValueTask Handle(RedisOutboxPoisonMessage message, CancellationToken cancellationToken)
    {
        if (Throw)
            throw new InvalidOperationException("poison handler failure");

        _messages.Enqueue(message);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A fixture with a recording poison handler. The base fixture would register the default drop
/// handler; the derived constructor replaces it before the provider is built.
/// </summary>
public sealed class OutboxRedisPoisonFixture : OutboxRedisFixture
{
    public RecordingPoisonHandler Handler { get; } = new();

    public OutboxRedisPoisonFixture()
    {
        Services.RemoveAll<IRedisOutboxPoisonHandler>();
        Services.AddSingleton<IRedisOutboxPoisonHandler>(Handler);
    }
}

public class OutboxRedisPoisonTests(OutboxRedisPoisonFixture fixture) : IClassFixture<OutboxRedisPoisonFixture>
{
    private static int s_tenant = 3000;

    private static int NextTenant() => Interlocked.Increment(ref s_tenant);

    private IServiceProvider Services => fixture.ServiceProvider;

    private IDatabase Database => Services.GetRequiredService<IDatabase>();

    private IOutboxDeliveryManager Manager => Services.GetRequiredService<IOutboxDeliveryManager>();

    private IOutboxMessagePublisher Publisher => Services.GetRequiredService<IOutboxMessagePublisher>();

    private RedisOutboxSettings Settings => Services.GetRequiredService<RedisOutboxSettings>();

    [Fact]
    public async Task MaxAttempts_InvokesHandlerAndAcknowledges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        fixture.Handler.Reset();

        try
        {
            Settings.ConsumeSettings.PendingClaimIdle = TimeSpan.FromMilliseconds(50);
            Settings.ConsumeSettings.MaxDeliveryAttempts = 1;

            await Publisher.Publish([new TestMessage("poison")], tenant, ct);

            OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "poison-handler-group");
            IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

            Assert.Equal(1, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct));

            await Task.Delay(250, ct);

            OutboxMessageFilter later = OutboxRedisFixture.CreateFilter<TestMessage>(
                tenant, filter.ConsumerGroupId, DateTimeOffset.UtcNow);

            Assert.Equal(0, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), later, ct));

            RedisOutboxPoisonMessage message = Assert.Single(fixture.Handler.Messages);

            Assert.Equal("poison", message.PayloadId);
            Assert.Equal(tenant, message.TenantId);
            Assert.Equal(filter.ConsumerGroupId, message.ConsumerGroup);
            Assert.Equal(filter.Part, message.Part);
            Assert.Equal(2, message.Attempt);
            Assert.Equal(DeliveryStatusCode.MaximumAttemptsError, message.StatusCode);
            Assert.Null(message.Exception);
            Assert.Contains("poison", Encoding.UTF8.GetString(message.Payload.Span));

            StreamPendingInfo pending = await Database.StreamPendingAsync(
                StreamKey(tenant, filter), filter.ConsumerGroupId);
            Assert.Equal(0, pending.PendingMessageCount);
        }
        finally
        {
            Settings.ConsumeSettings.PendingClaimIdle = RedisOutboxConsumeSettings.Defaults.PendingClaimIdle;
            Settings.ConsumeSettings.MaxDeliveryAttempts = RedisOutboxConsumeSettings.Defaults.MaxDeliveryAttempts;
            fixture.Handler.Reset();
        }
    }

    [Fact]
    public async Task PermanentOutcome_InvokesHandlerWithTheConsumerException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        fixture.Handler.Reset();

        await Publisher.Publish([new TestMessage("broken", 42)], tenant, ct);

        OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "permanent-handler-group");
        IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

        Assert.Equal(1, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct));

        buffer[0].Error(new InvalidOperationException("boom"), "permanent");
        Assert.Equal(1, await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, 1), filter, ct));

        RedisOutboxPoisonMessage message = Assert.Single(fixture.Handler.Messages);

        Assert.Equal("broken", message.PayloadId);
        Assert.Equal(DeliveryStatusCode.Error, message.StatusCode);
        Assert.Equal("permanent", message.StatusMessage);
        Assert.NotNull(message.Exception);
        Assert.Contains("boom", message.Exception!.Message);
        Assert.Contains("broken", Encoding.UTF8.GetString(message.Payload.Span));

        StreamPendingInfo pending = await Database.StreamPendingAsync(
            StreamKey(tenant, filter), filter.ConsumerGroupId);
        Assert.Equal(0, pending.PendingMessageCount);
    }

    [Fact]
    public async Task HandlerFailure_LeavesTheEntryPending()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int tenant = NextTenant();

        fixture.Handler.Reset();
        fixture.Handler.Throw = true;

        try
        {
            await Publisher.Publish([new TestMessage("keep-me")], tenant, ct);

            OutboxMessageFilter filter = OutboxRedisFixture.CreateFilter<TestMessage>(tenant, "failing-handler-group");
            IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

            Assert.Equal(1, await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromMinutes(1), filter, ct));

            buffer[0].Error(new InvalidOperationException("boom"), "permanent");
            await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, 1), filter, ct);

            // The handler threw, so the provider must not acknowledge: the message stays available.
            Assert.Empty(fixture.Handler.Messages);

            StreamPendingInfo pending = await Database.StreamPendingAsync(
                StreamKey(tenant, filter), filter.ConsumerGroupId);
            Assert.Equal(1, pending.PendingMessageCount);
        }
        finally
        {
            fixture.Handler.Reset();
        }
    }

    private RedisKey StreamKey(int tenant, OutboxMessageFilter filter)
        => Settings.KeySettings.StreamKey(tenant, filter.Part, filter.PayloadType);
}
