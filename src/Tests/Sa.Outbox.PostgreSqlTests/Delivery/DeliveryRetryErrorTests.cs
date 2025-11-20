using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

public class DeliveryRetryErrorTests(DeliveryRetryErrorTests.Fixture fixture) : IClassFixture<DeliveryRetryErrorTests.Fixture>
{

    public class TestException(string message) : Exception(message)
    {
    }


    public class TestMessageConsumer : IConsumer<TestMessage>
    {
        private static readonly TestException s_err = new("test same error");

        public async ValueTask Consume(IReadOnlyCollection<IOutboxContextOperations<TestMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            foreach (var msg in outboxMessages)
            {
                msg.Error(s_err, "test");
            }
        }
    }


    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        public Fixture() : base()
        {
            Services
                .AddOutbox(builder => builder
                    .WithPartitioningSupport((_, sp)
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
    public async Task Deliver_RetriesOnErrorProcess_MustBe_Logged_501()
    {
        Console.Write(fixture.ConnectionString, TestContext.Current.CancellationToken);

        List<TestMessage> messages = [new TestMessage { PayloadId = "11", Content = "Message 1", TenantId = 1 }];

        ulong cnt = await fixture.Publisher.Publish(messages, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);

        const int MaxDeliveryAttempts = 2;

        var settings = new OutboxDeliverySettings(Guid.NewGuid())
        {
            ExtractSettings =
            {
                ForEachTenant = true,
                LockDuration = TimeSpan.FromMilliseconds(0),
                LockRenewal = TimeSpan.FromMinutes(10)
            },
            ConsumeSettings =
            {
                MaxDeliveryAttempts = MaxDeliveryAttempts
            }
        };

        cnt = 0;
        while (cnt < MaxDeliveryAttempts)
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);

            await Sub.ProcessMessages<TestMessage>(settings, CancellationToken.None);
            int attempt = await GetDeliveries();
            if (attempt > MaxDeliveryAttempts)
            {
                cnt++;
            }
        }

        int errCount = await fixture.DataSource.ExecuteReaderFirst<int>("select count(error_id) from outbox__$error", TestContext.Current.CancellationToken);
        Assert.Equal(1, errCount);

        string delivery_id = await fixture.DataSource.ExecuteReaderFirst<string>("select delivery_id from outbox__$delivery where delivery_status_code = 501", TestContext.Current.CancellationToken);
        Assert.NotEmpty(delivery_id);

        string outbox_delivery_id = await fixture.DataSource.ExecuteReaderFirst<string>("SELECT outbox_delivery_id FROM public.outbox WHERE outbox_delivery_status_code = 501", TestContext.Current.CancellationToken);
        Assert.Equal(delivery_id, outbox_delivery_id);
    }

    private Task<int> GetDeliveries() => fixture.DataSource.ExecuteReaderFirst<int>("select count(delivery_id) from outbox__$delivery", TestContext.Current.CancellationToken);
}
