using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

public class DeliveryPermanentErrorTests(DeliveryPermanentErrorTests.Fixture fixture) : IClassFixture<DeliveryPermanentErrorTests.Fixture>
{

    public class TestException(string message) : Exception(message)
    {
    }


    public class TestMessageConsumer : IConsumer<TestMessage>
    {
        private static readonly TestException s_err = new("test permanent error");

        public async ValueTask Consume(IReadOnlyCollection<IOutboxContext<TestMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            foreach (IOutboxContext<TestMessage> msg in outboxMessages)
            {
                msg.PermanentError(s_err, "test");
            }
        }
    }


    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        public Fixture() : base()
        {
            Services
                .AddOutbox(builder
                    => builder.WithPartitioningSupport((_, sp)
                        => sp.GetTenantIds = t => Task.FromResult<int[]>([1, 2])
                )
                .WithDeliveries(builder
                    => builder.AddDelivery<TestMessageConsumer, TestMessage>()
                )
            );
        }

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }


    private IDeliveryProcessor Sub => fixture.Sub;


    [Fact]
    public async Task Deliver_ErrorProcess_MustBe_Logged()
    {
        Console.Write(fixture.ConnectionString, TestContext.Current.CancellationToken);

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
                ForEachTenant = true,
            }
        };

        var result = await Sub.ProcessMessages<TestMessage>(settings, CancellationToken.None);

        Assert.Equal(0, result);


        int errCount = await fixture.DataSource.ExecuteReaderFirst<int>("select count(error_id) from outbox__$error", TestContext.Current.CancellationToken);

        Assert.Equal(1, errCount);
    }
}
