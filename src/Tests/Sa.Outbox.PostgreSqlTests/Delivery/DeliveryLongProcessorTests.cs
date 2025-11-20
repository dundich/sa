using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

public class DeliveryLongProcessorTests(DeliveryLongProcessorTests.Fixture fixture) : IClassFixture<DeliveryLongProcessorTests.Fixture>
{
    public class TestMessageConsumer : IConsumer<TestMessage>
    {
        public async ValueTask Consume(IReadOnlyCollection<IOutboxContextOperations<TestMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            Console.WriteLine(outboxMessages.Count);
            await Task.Delay(1000, cancellationToken);
        }
    }


    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        public Fixture() : base()
        {
            Services.AddOutbox(builder
                => builder
                    .WithPartitioningSupport((_, sp)
                        => sp.GetTenantIds = t => Task.FromResult<int[]>([1, 2])
                    )
                    .WithDeliveries(builder
                        => builder.AddDelivery<TestMessageConsumer, TestMessage>()
                    )
            )
            ;
        }

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }


    private IDeliveryProcessor Sub => fixture.Sub;


    [Fact]
    public async Task Deliver_LongProcess_MustBe_Work()
    {
        Console.Write(fixture.ConnectionString);

        List<TestMessage> messages =
        [
            new TestMessage { PayloadId = "11", Content = "Message 1", TenantId = 1},
            new TestMessage { PayloadId = "12", Content = "Message 2", TenantId = 2}
        ];

        var cnt = await fixture.Publisher.Publish(messages, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);

        var settings = new OutboxDeliverySettings(Guid.NewGuid())
        {
            ExtractSettings =
            {
                LockDuration = TimeSpan.FromMilliseconds(300),
                LockRenewal = TimeSpan.FromMilliseconds(100),
                ForEachTenant = true,
            }
        };

        var result = await Sub.ProcessMessages<TestMessage>(settings, CancellationToken.None);
        Assert.True(result > 0);
    }
}
