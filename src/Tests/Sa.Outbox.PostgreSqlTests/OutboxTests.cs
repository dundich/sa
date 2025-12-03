using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Support;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests;

public class OutBoxTests(OutBoxTests.Fixture fixture) : IClassFixture<OutBoxTests.Fixture>
{
    public class SomeMessage : IOutboxPayloadMessage
    {
        public static string PartName => "some";

        public string PayloadId { get; set; } = default!;
        public int TenantId { get; set; }

    }

    public class SomeMessageConsumer : IConsumer<SomeMessage>
    {
        static int s_Counter = 0;

        public async ValueTask Consume(IReadOnlyCollection<IOutboxContextOperations<SomeMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref s_Counter, outboxMessages.Count);
            await Task.Delay(100, cancellationToken);
        }

        public static int Counter => s_Counter;
    }

    public class Fixture : PgDataSourceFixture
    {
        public Fixture() : base()
        {
            Services
                .AddOutbox(builder => builder
                    .WithPartitioningSupport((_, sp) =>
                    {
                        sp.ForEachTenant = true;
                        sp.GetTenantIds = t => Task.FromResult<int[]>([1, 2]);
                    })
                    .WithDeliveries(builder => builder
                        .AddDelivery<SomeMessageConsumer, SomeMessage>((_, settings) =>
                        {
                            settings.ScheduleSettings.ExecutionInterval = TimeSpan.FromMilliseconds(100);
                            settings.ScheduleSettings.InitialDelay = TimeSpan.Zero;
                            settings.ConsumeSettings.MaxBatchSize = 1;
                        })
                    )
                )
                .AddOutboxUsingPostgreSql(cfg =>
                {
                    cfg.ConfigureDataSource(c => c.WithConnectionString(_ => this.ConnectionString));
                    cfg.ConfigureOutboxSettings((_, settings) =>
                    {
                        settings.TableSettings.DatabaseSchemaName = "test";
                        settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(1);
                    });
                    cfg.WithMessageSerializer(sp => new OutboxMessageSerializer());
                })
            ;
        }
    }


    IServiceProvider ServiceProvider => fixture.ServiceProvider;


    [Fact]
    public async Task OutBoxTest()
    {
        Console.WriteLine(fixture.ConnectionString);

        // start cron schedules
        var scheduler = ServiceProvider.GetRequiredService<IScheduler>();
        int i = scheduler.Start(CancellationToken.None);
        Assert.True(i > 0);

        // start delivery message
        var publisher = ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        ulong total = await publisher.Publish([
            new SomeMessage { TenantId = 1 }
            , new SomeMessage { TenantId = 1 }
            , new SomeMessage { TenantId = 1 }
            , new SomeMessage { TenantId = 1 }
        ], TestContext.Current.CancellationToken);

        var migrationService = ServiceProvider.GetRequiredService<IPartMigrationService>();

        bool r = await migrationService.WaitMigration(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(r, "none migration");

        // delay for consume
        int j = 0;
        while (SomeMessageConsumer.Counter < (int)total && j++ < 10)
        {
            // delay for consume
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        await scheduler.Stop();

        Assert.True(SomeMessageConsumer.Counter > 0);
    }
}
