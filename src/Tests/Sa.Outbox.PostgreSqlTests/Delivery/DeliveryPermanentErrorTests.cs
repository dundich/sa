using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Configuration;
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
            OutboxConsumerSettings settings,
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
                .AddSaOutbox(builder => builder
                    .WithTenants((_, sp) => sp.WithTenantIds(1, 2))
                    .WithDeliveries(builder => builder
                        .AddDeliveryScoped<TestMessageConsumer, TestMessage>("test2", (_, s) =>
                        {
                            OutboxSettings = s.WithNoBatchingWindow().Build();
                        })
                )
            );
        }

        public OutboxConsumerSettings OutboxSettings = default!;

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

        var cnt = await fixture.Publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);

        var result = await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);

        // Both messages were handled, even though both ended in a permanent error — the count is
        // handled messages, not successes.
        Assert.Equal(2, result);

        var sql = $"select count(*) from {_tableSettings.Error.TableName}";
        int errCount = await fixture.DataSource.ExecuteReaderFirst<int>(sql, TestContext.Current.CancellationToken);

        // One row, not two: both messages carry the same exception instance, so they de-duplicate.
        Assert.Equal(1, errCount);
    }
}
