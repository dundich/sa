using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

public class DeliveryPermanentErrorTests(DeliveryPermanentErrorTests.Fixture fixture)
    : IClassFixture<DeliveryPermanentErrorTests.Fixture>
{

    public class TestException(string message) : Exception(message)
    {
    }


    class TestMessageConsumer : IConsumer<TestMessage>
    {
        private static readonly TestException s_err = new("test permanent error");
        public async ValueTask Consume(
            ConsumerGroupSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            foreach (var msg in messages.Span)
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
                .AddOutbox(builder
                    => builder.WithPartitioningSupport((_, sp)
                        => sp.WithTenantIds(1, 2)
                )
                .WithDeliveries(builder
                    => builder.AddDeliveryScoped<TestMessageConsumer, TestMessage>("test2", (_, s) =>
                    {
                        s.ConsumeSettings
                            .WithNoBatchingWindow();
                        OutboxSettings = s;
                    })
                )
            );
        }

        public ConsumerGroupSettings OutboxSettings = default!;

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }

    private readonly PgOutboxTableSettings _tableSettings = new();

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

        var result = await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);

        Assert.Equal(0, result);

        var sql = $"select count(*) from {_tableSettings.Error.TableName}";
        int errCount = await fixture.DataSource.ExecuteReaderFirst<int>(sql, TestContext.Current.CancellationToken);

        Assert.Equal(1, errCount);
    }
}
