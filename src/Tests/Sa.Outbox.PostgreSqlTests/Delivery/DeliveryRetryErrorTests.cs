using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

public class DeliveryRetryErrorTests(DeliveryRetryErrorTests.Fixture fixture)
    : IClassFixture<DeliveryRetryErrorTests.Fixture>
{
    public class TestException(string message) : Exception(message) { }

    class TestMessageConsumer : IConsumer<TestMessage>
    {
        private static readonly TestException s_err = new("test same error");

        public async ValueTask Consume(
            ConsumerGroupSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> outboxMessages,
            CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            foreach (var msg in outboxMessages.Span)
            {
                msg.Warn(s_err, "test");
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
                        => sp.WithTenantIds(1)
                    )
                    .WithDeliveries(builder
                        => builder.AddDelivery<TestMessageConsumer, TestMessage>("test4", (_, s) =>
                        {
                            s.ConsumeSettings
                                .WithNoLockDuration()
                                .WithLockRenewal(TimeSpan.FromMinutes(10))
                                .WithMaxDeliveryAttempts(MaxDeliveryAttempts)
                                .WithNoBatchingWindow();

                            OutboxSettings = s;
                        })
                    )
                );
        }

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        public const int MaxDeliveryAttempts = 2;

        public ConsumerGroupSettings OutboxSettings { get; private set; } = default!;
    }



    private IDeliveryProcessor Sub => fixture.Sub;

    private readonly PgOutboxTableSettings _tableSettings = new();


    [Fact]
    public async Task Deliver_RetriesOnErrorProcess_MustBe_Logged_501()
    {
        Console.Write(fixture.ConnectionString, TestContext.Current.CancellationToken);

        List<TestMessage> messages = [new TestMessage { PayloadId = "1", Content = "Message 1", TenantId = 1 }];

        ulong cnt = await fixture.Publisher.Publish(messages, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);


        cnt = 0;
        while (cnt < Fixture.MaxDeliveryAttempts)
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);

            await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);
            int attempt = await GetDeliveries();
            if (attempt > Fixture.MaxDeliveryAttempts)
            {
                cnt++;
            }
        }

        int errCount = await fixture.DataSource.ExecuteReaderFirst<int>($"select count(error_id) from {_tableSettings.DatabaseErrorTableName}", TestContext.Current.CancellationToken);
        Assert.Equal(1, errCount);

        var delivery_id = await fixture.DataSource.ExecuteReaderFirst<long>($"select delivery_id from {_tableSettings.DatabaseDeliveryTableName} where delivery_status_code = 501", TestContext.Current.CancellationToken);
        Assert.NotEqual(0, delivery_id);

        var outbox_delivery_id = await fixture.DataSource.ExecuteReaderFirst<long>($"SELECT delivery_id FROM  {_tableSettings.DatabaseTableName} WHERE delivery_status_code = 501", TestContext.Current.CancellationToken);
        Assert.Equal(delivery_id, outbox_delivery_id);
    }

    private Task<int> GetDeliveries() => fixture.DataSource.ExecuteReaderFirst<int>($"select count(delivery_id) from {_tableSettings.DatabaseDeliveryTableName}", TestContext.Current.CancellationToken);
}
