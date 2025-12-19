using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

public class DeliveryProcessorTests(DeliveryProcessorTests.Fixture fixture)
    : IClassFixture<DeliveryProcessorTests.Fixture>
{
    class TestMessageConsumer : IConsumer<TestMessage>
    {
        public async ValueTask Consume(
            OutboxDeliverySettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> outboxMessages,
            CancellationToken cancellationToken)
        {
            Console.WriteLine(outboxMessages.Length);
            await Task.Delay(100, cancellationToken);
        }
    }


    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        public Fixture() : base()
        {
            Services
                .AddOutbox(builder
                    => builder.WithPartitioningSupport((_, ps)
                        => ps.WithTenantIds(1, 2)
                )
                .WithDeliveries(builder
                    => builder.AddDelivery<TestMessageConsumer, TestMessage>("test3", (_, s) =>
                    {
                        s.ConsumeSettings.WithBatchingWindow(TimeSpan.FromMinutes(3));
                        OutboxSettings = s;
                    })
                )
            );
        }

        public OutboxDeliverySettings OutboxSettings { get; set; } = default!;

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }


    private IDeliveryProcessor Sub => fixture.Sub;


    [Fact]
    public async Task Deliver_Process_MustBe_Work()
    {
        Console.Write(fixture.ConnectionString);

        List<TestMessage> messages =
        [
            new TestMessage { PayloadId = "11", Content = "Message 1", TenantId = 1},
            new TestMessage { PayloadId = "12", Content = "Message 2", TenantId = 2}
        ];

        var cnt = await fixture.Publisher.Publish(messages, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);


        var result = await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);
        Assert.Equal(0, result);


        fixture.OutboxSettings.ConsumeSettings.WithNoBatchingWindow();

        result = await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);
        Assert.True(result > 0);
    }
}
