using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

public class DeliveryWithAutoTenantDetectionTests(DeliveryWithAutoTenantDetectionTests.Fixture fixture)
    : IClassFixture<DeliveryWithAutoTenantDetectionTests.Fixture>
{
    class TestConsumer : IConsumer<TestMessage>
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

    class TenantStubDetector(ISelectTenantCommand command) : IOutboxTenantDetector
    {
        public static int CountTenant = 0;

        public bool CanDetect => true;

        public async ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
        {
            var rows = await command.Execute(cancellationToken);
            Interlocked.Exchange(ref CountTenant, rows.Count);
            return [.. rows];
        }
    }

    public class Fixture : PgDataSourceFixture<IDeliveryProcessor>
    {
        public Fixture() : base()
        {
            Services
                .AddOutbox(builder => builder
                    .WithTenantSettings((_, s) => s.WithAutoDetect())
                    .WithDeliveries(b => b
                        .AddDelivery<TestConsumer, TestMessage>("test_auto_detect", (_, s) =>
                        {
                            s.ConsumeSettings
                                .WithNoBatchingWindow()
                                .WithNoLockDuration()
                                ;

                            OutboxSettings = s;
                        })
                    )
                )
                .AddOutboxUsingPostgreSql(builder => builder
                    .WithDataSource(b => b.WithConnectionString(_ => ConnectionString))
                    .WithMessageSerializer(_ => OutboxMessageSerializer.Instance)
                    .WithOutboxSettings((_, pg) => pg.TableSettings.WithSchema("auto_detect"))
                );

            Services
                .RemoveAll<IOutboxTenantDetector>()
                .AddSingleton<IOutboxTenantDetector, TenantStubDetector>();
        }

        public ConsumerGroupSettings OutboxSettings { get; set; } = default!;

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }


    private IDeliveryProcessor Sub => fixture.Sub;


    [Fact]
    public async Task DeliverProcessWithAutoTenantModeMustBeWork()
    {
        Console.Write(fixture.ConnectionString);

        List<TestMessage> messages =
        [
            new TestMessage { PayloadId = "01", Content = "M 1", TenantId = 1},
            new TestMessage { PayloadId = "02", Content = "M 2", TenantId = 2},
            new TestMessage { PayloadId = "03", Content = "M 3", TenantId = 3}
        ];

        var cnt = await fixture.Publisher.Publish(messages, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);

        var result = await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);
        Assert.True(result > 0);

        var countTenants = messages.Select(c => c.TenantId).Distinct().Count();

        Assert.Equal(countTenants, TenantStubDetector.CountTenant);
    }
}
