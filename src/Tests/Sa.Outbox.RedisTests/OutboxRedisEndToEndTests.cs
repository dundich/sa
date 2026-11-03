using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.Publication;
using Sa.Schedule;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// A complete delivery group on top of <see cref="OutboxRedisFixture"/>: the test drives the real
/// scheduler, so the whole wiring (job, consumer manager, rent, finish) is exercised, not just the
/// delivery manager.
/// </summary>
public class OutboxRedisEndToEndFixture : OutboxRedisFixture
{
    public OutboxRedisEndToEndFixture()
    {
        Services.AddSaOutbox(builder => builder
            .WithTenants((_, tenants) => tenants.WithTenantIds(1))
            .WithDeliveries(deliveries => deliveries
                .AddDeliveryScoped<SomeMessageConsumer, TestMessage>("e2e-group", (_, group) => group
                    // The default InitialDelay is well beyond a test's wait budget.
                    .StartImmediately()
                    .WithInterval(TimeSpan.FromMilliseconds(100))
                    .WithMaxBatchSize(4)
                    .WithNoBatchingWindow())));
    }

    public sealed class SomeMessageConsumer : IConsumer<TestMessage>
    {
        private static int s_consumed;

        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            Interlocked.Add(ref s_consumed, messages.Length);

            foreach (IOutboxContextOperations<TestMessage> context in messages.Span)
                context.Ok();

            return ValueTask.CompletedTask;
        }

        public static int Consumed => Volatile.Read(ref s_consumed);
    }
}

public class OutboxRedisEndToEndTests(OutboxRedisEndToEndFixture fixture) : IClassFixture<OutboxRedisEndToEndFixture>
{
    [Fact]
    public async Task Scheduler_DeliversPublishedMessages()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IServiceProvider services = fixture.ServiceProvider;

        IScheduler scheduler = services.GetRequiredKeyedService<IScheduler>(OutboxScope.DefaultKey);
        int started = await scheduler.Start(ct);
        Assert.True(started > 0, "the outbox schedule must start at least one job");

        try
        {
            IOutboxMessagePublisher publisher = services.GetRequiredService<IOutboxMessagePublisher>();
            ulong published = await publisher.Publish(
                [new TestMessage("1"), new TestMessage("2"), new TestMessage("3")], 1, ct);
            Assert.Equal(3UL, published);

            for (int i = 0; i < 20 && OutboxRedisEndToEndFixture.SomeMessageConsumer.Consumed < 3; i++)
                await Task.Delay(300, ct);

            Assert.Equal(3, OutboxRedisEndToEndFixture.SomeMessageConsumer.Consumed);
        }
        finally
        {
            await scheduler.Stop();
        }
    }
}
