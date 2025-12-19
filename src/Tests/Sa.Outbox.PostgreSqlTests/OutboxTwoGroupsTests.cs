using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Support;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests;

public class OutboxTwoGroupsTests(OutboxTwoGroupsTests.Fixture fixture) : IClassFixture<OutboxTwoGroupsTests.Fixture>
{
    class SomeMessage : IOutboxPayloadMessage
    {
        public static string PartName => "some";

        public string PayloadId { get; } = Guid.NewGuid().ToString();
        public int TenantId { get; set; }
    }

    class SomeMessageConsumerGr1 : IConsumer<SomeMessage>
    {
        public static int Counter;

        public async ValueTask Consume(
            ConsumeSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<SomeMessage>> outboxMessages,
            CancellationToken cancellationToken)
        {
            Interlocked.Add(ref Counter, outboxMessages.Length);
            await Task.Delay(100, cancellationToken);
        }
    }

    class SomeMessageConsumerGr2 : IConsumer<SomeMessage>
    {
        public static int Counter;

        public async ValueTask Consume(
            ConsumeSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<SomeMessage>> outboxMessages,
            CancellationToken cancellationToken)
        {
            Interlocked.Add(ref Counter, outboxMessages.Length);
            await Task.Delay(100, cancellationToken);
        }
    }

    public class Fixture : PgDataSourceFixture
    {
        public Fixture() : base()
        {
            Services
                .AddOutbox(builder => builder
                    .WithPartitioningSupport((_, sp) => sp.WithTenantIds(1, 2))
                    .WithDeliveries(deliveryBuilder => deliveryBuilder

                        .AddDelivery<SomeMessageConsumerGr1, SomeMessage>("test_gr1", (_, settings) =>
                        {
                            settings.ScheduleSettings
                                .WithInterval(TimeSpan.FromMilliseconds(100))
                                .WithInitialDelay(TimeSpan.Zero);

                            settings.ConsumeSettings
                                .WithNoBatchingWindow();
                        })
                        .AddDelivery<SomeMessageConsumerGr2, SomeMessage>("test_gr2", (_, settings) =>
                        {
                            settings.ScheduleSettings
                                .WithInterval(TimeSpan.FromMilliseconds(100))
                                .WithInitialDelay(TimeSpan.Zero);

                            settings.ConsumeSettings
                                .WithNoBatchingWindow();
                        })
                    )
                )
                .AddOutboxUsingPostgreSql(cfg =>
                {
                    cfg.ConfigureDataSource(c => c.WithConnectionString(_ => this.ConnectionString));
                    cfg.ConfigureOutboxSettings((_, settings) =>
                    {
                        settings.TableSettings.DatabaseSchemaName = "test_gr";
                        settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(1);
                    });
                    cfg.WithMessageSerializer(sp => new OutboxMessageSerializer());
                });
        }
    }

    IServiceProvider ServiceProvider => fixture.ServiceProvider;

    [Fact]
    public async Task OutBox_TwoGroups_ShouldProcessSeparately()
    {
        Console.WriteLine(fixture.ConnectionString);

        SomeMessageConsumerGr1.Counter = 0;
        SomeMessageConsumerGr2.Counter = 0;

        var scheduler = ServiceProvider.GetRequiredService<IScheduler>();
        int startedSchedules = scheduler.Start(CancellationToken.None);
        Assert.True(startedSchedules >= 2, "Ожидалось как минимум 2 расписания (по одному на группу)");

        var publisher = ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
        var messages = new[]
        {
            new SomeMessage { TenantId = 1 },
            new SomeMessage { TenantId = 1 },
            new SomeMessage { TenantId = 2 },
            new SomeMessage { TenantId = 1 }
        };

        ulong total = await publisher.Publish(messages, TestContext.Current.CancellationToken);

        var migrationService = ServiceProvider.GetRequiredService<IPartMigrationService>();
        bool migrated = await migrationService.WaitMigration(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(migrated, "Миграция не завершилась вовремя");


        int attempts = 0;
        const int maxAttempts = 20;
        while ((SomeMessageConsumerGr1.Counter < (int)total || SomeMessageConsumerGr2.Counter < (int)total) && attempts++ < maxAttempts)
        {
            await Task.Delay(400, TestContext.Current.CancellationToken);
        }

        await Task.Delay(200, TestContext.Current.CancellationToken);

        await scheduler.Stop();

        Assert.Equal((int)total, SomeMessageConsumerGr1.Counter);
        Assert.Equal((int)total, SomeMessageConsumerGr2.Counter);
    }
}