using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

public class DeliveryBatchingWindowTests(DeliveryBatchingWindowTests.Fixture fixture)
    : IClassFixture<DeliveryBatchingWindowTests.Fixture>
{
    class TestMessageConsumer : IConsumer<TestMessage>
    {
        public async ValueTask Consume(
            ConsumerGroupSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            Console.WriteLine(messages.Length);
            await Task.Delay(100, cancellationToken);
        }
    }

    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        public Fixture() : base()
        {
            Services
                .AddSaOutbox(b => b
                    .WithTenants((_, s) => s.WithTenantIds(1, 2))
                    .WithDeliveries(builder => builder
                        .AddDeliveryScoped<TestMessageConsumer, TestMessage>("test3", (_, s) =>
                        {
                            s.ConsumeSettings.WithBatchingWindow(TimeSpan.FromMinutes(3));
                            OutboxSettings = s;
                        })
                )
            );
        }

        public ConsumerGroupSettings OutboxSettings { get; set; } = default!;

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }


    private IDeliveryProcessor Sub => fixture.Sub;


    [Fact]
    public async Task Deliver_Process_MustBe_Work()
    {
        Console.Write(fixture.ConnectionString);


        var cnt = await fixture.Publisher.PublishSingle(new TestMessage { PayloadId = "11", Content = "Message 1", TenantId = 1 }, 1, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);

        cnt = await fixture.Publisher.PublishSingle(new TestMessage { PayloadId = "12", Content = "Message 2", TenantId = 2 }, 2, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);


        var result = await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);
        Assert.Equal(0, result);


        fixture.OutboxSettings.ConsumeSettings.WithNoBatchingWindow();

        result = await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);
        Assert.True(result > 0);
    }
}
