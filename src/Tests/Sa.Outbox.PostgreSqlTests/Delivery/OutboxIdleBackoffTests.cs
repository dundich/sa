using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Publication;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

/// <summary>
/// End-to-end wiring of the empty-queue back-off: a real PostgreSQL-backed outbox, a real
/// <c>Sa.Schedule</c> scheduler, and the per-group <see cref="OutboxIdleState"/> that the
/// scheduled <c>DeliveryJob</c> reports into.
/// </summary>
public sealed class OutboxIdleBackoffTests(OutboxIdleBackoffTests.Fixture fixture)
    : IClassFixture<OutboxIdleBackoffTests.Fixture>
{
    private static readonly TimeSpan BaseInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(2400);

    internal sealed class IdleConsumer : IConsumer<TestMessage>
    {
        public static int Counter;

        public static void Reset() => Counter = 0;

        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            Interlocked.Add(ref Counter, messages.Length);
            return ValueTask.CompletedTask;
        }
    }

    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        internal const string GroupName = "idle_backoff";
        internal const int TenantId = 7;

        /// <summary>The settings as built by <c>AddDeliveryJob</c> (sanitized group id included).</summary>
        internal OutboxConsumerSettings ConsumerSettings { get; private set; } = default!;

        public Fixture() : base()
        {
            Services.AddSaOutbox(builder => builder
                .WithMetadata((_, b) => b.AddMetadata<TestMessage>("root_1", m => m.PayloadId))
                .WithTenants((_, s) => s.WithTenantIds(TenantId))
                .WithDeliveries(deliveryBuilder => deliveryBuilder
                    .AddDeliveryScoped<IdleConsumer, TestMessage>(GroupName, (_, b) =>
                    {
                        // StartImmediately: the default InitialDelay is 10s, beyond this test's
                        // 8s wait budget for the scheduled job's first run.
                        b.StartImmediately()
                         .WithInterval(BaseInterval)
                         .WithIdleBackoff(MaxInterval)
                         .WithNoBatchingWindow();

                        ConsumerSettings = b.Build();
                    })
                )
            );
        }

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }

    [Fact]
    public async Task EmptyQueue_BacksOff_AndStateIsWiredFromSettings()
    {
        IdleConsumer.Reset();
        var ct = TestContext.Current.CancellationToken;

        var scheduler = fixture.ServiceProvider.GetRequiredKeyedService<IScheduler>(OutboxScope.DefaultKey);
        Assert.True(await scheduler.Start(ct) >= 1);

        var registry = fixture.ServiceProvider.GetRequiredService<IOutboxIdleStateRegistry>();
        var state = registry.Get(fixture.ConsumerSettings.ConsumerGroupId);
        Assert.NotNull(state);

        var baseInterval = fixture.ConsumerSettings.Interval;
        var maxInterval = fixture.ConsumerSettings.IdleMaxInterval;
        Assert.Equal(BaseInterval, baseInterval);
        Assert.Equal(MaxInterval, maxInterval);

        // Live proof: nothing was published, so the scheduled job keeps reporting empty polls and
        // the wait must grow beyond the base interval (and stay within the configured bound).
        await WaitForAsync(() => state!.CurrentInterval > baseInterval, ct);
        Assert.InRange(state!.CurrentInterval, baseInterval, maxInterval);
        Assert.True(state.ConsecutiveEmptyPolls >= 2,
            $"expected at least two consecutive empty polls, got {state.ConsecutiveEmptyPolls}");

        // Work resets the back-off to the base interval.
        state.Report(1);
        Assert.Equal(baseInterval, state.CurrentInterval);
        Assert.Equal(0, state.ConsecutiveEmptyPolls);

        await scheduler.Stop();
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct, int maxAttempts = 40)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
            await Task.Delay(200, ct);

        Assert.True(condition(), "condition not met within the wait budget");
    }
}
