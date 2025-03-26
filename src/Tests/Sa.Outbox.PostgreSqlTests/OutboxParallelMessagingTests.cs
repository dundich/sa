using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Support;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests;

public class OutboxParallelMessagingTests(OutboxParallelMessagingTests.Fixture fixture) : IClassFixture<OutboxParallelMessagingTests.Fixture>
{
    static class GenMessageRange
    {

        public const int Threads = 5;
        const int From = 10;
        const int To = 100;

        public static int GetMessageCount() => Random.Shared.Next(From, To);
    }


    [OutboxMessage(part: "multi_1")]
    public class SomeMessage1 : IOutboxPayloadMessage
    {
        public string Message { get; set; } = Guid.NewGuid().ToString();
        public int TenantId { get; set; } = Random.Shared.Next(1, 2);
    }

    [OutboxMessage(part: "multi_2")]
    public class SomeMessage2 : IOutboxPayloadMessage
    {
        public string Message { get; set; } = Guid.NewGuid().ToString();
        public int TenantId { get; set; } = Random.Shared.Next(1, 2);
    }


    public static class CommonCounter
    {
        static int s_counter = 0;

        public static void Add(int count)
        {
            Interlocked.Add(ref s_counter, count);
        }
        public static int Counter => s_counter;
    }

    public class SomeMessageConsumer1 : IConsumer<SomeMessage1>
    {
        public ValueTask Consume(IReadOnlyCollection<IOutboxContext<SomeMessage1>> outboxMessages, CancellationToken cancellationToken)
        {
            CommonCounter.Add(outboxMessages.Count);
            return ValueTask.CompletedTask;
        }
    }


    public class SomeMessageConsumer2 : IConsumer<SomeMessage2>
    {
        public ValueTask Consume(IReadOnlyCollection<IOutboxContext<SomeMessage2>> outboxMessages, CancellationToken cancellationToken)
        {
            CommonCounter.Add(outboxMessages.Count);
            return ValueTask.CompletedTask;
        }
    }

    public class Fixture : PgDataSourceFixture
    {
        public Fixture() : base()
        {
            Services
                .AddOutbox(builder =>
                {
                    builder
                    .WithPartitioningSupport((_, sp) =>
                    {
                        sp.ForEachTenant = true;
                        sp.GetTenantIds = t => Task.FromResult<int[]>([1, 2]);
                    })
                    .WithDeliveries(builder => builder
                        .AddDelivery<SomeMessageConsumer1, SomeMessage1>((_, settings) =>
                        {
                            settings.ScheduleSettings.ExecutionInterval = TimeSpan.FromMilliseconds(500);
                            settings.ScheduleSettings.InitialDelay = TimeSpan.Zero;
                            settings.ExtractSettings.MaxBatchSize = 1024;
                        })
                        .AddDelivery<SomeMessageConsumer2, SomeMessage2>((_, settings) =>
                        {
                            settings.ScheduleSettings.ExecutionInterval = TimeSpan.FromMilliseconds(500);
                            settings.ScheduleSettings.InitialDelay = TimeSpan.Zero;
                            settings.ExtractSettings.MaxBatchSize = 1024;
                        })
                    );
                    builder.PublishSettings.MaxBatchSize = 1024;
                })
                .AddOutboxUsingPostgreSql(cfg =>
                {
                    cfg
                        .ConfigureDataSource(c => c.WithConnectionString(_ => this.ConnectionString))
                        .ConfigureOutboxSettings((_, settings) =>
                        {
                            settings.TableSettings.DatabaseSchemaName = "parallel";
                            settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(1);
                        })
                        .WithMessageSerializer(sp => new OutboxMessageSerializer());
                });
        }
    }

    IServiceProvider ServiceProvider => fixture.ServiceProvider;

    [Fact]
    public async Task ParallelMessaging_MustBeProcessed()
    {
        Console.WriteLine(fixture.ConnectionString, TestContext.Current.CancellationToken);

        // start cron schedules
        IScheduler scheduler = ServiceProvider.GetRequiredService<IScheduler>();
        int i = scheduler.Start(CancellationToken.None);
        Assert.True(i > 0);

        // start delivery message
        var publisher = ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        List<Task<long>> tasks = [
            RunPublish<SomeMessage1>(publisher)
           , RunPublish<SomeMessage2>(publisher)
        ];


        await Task.WhenAll(tasks);

        long total = tasks.Select(c => c.Result)
            .DefaultIfEmpty()
            .Aggregate((t1, t2) => t1 + t2);

        var migrationService = ServiceProvider.GetRequiredService<IPartMigrationService>();

        bool r = await migrationService.WaitMigration(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(r, "none migration");

        // delay for consume
        while (CommonCounter.Counter < (int)total)
        {
            // delay for consume
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        await scheduler.Stop();

        Assert.True(CommonCounter.Counter > 0);
    }

    private static async Task<long> RunPublish<T>(IOutboxMessagePublisher publisher)
        where T : IOutboxPayloadMessage, new()
    {
        long total = 0;
        List<int> nodes = [.. Enumerable.Range(1, GenMessageRange.Threads)];
        ParallelLoopResult loop = Parallel.ForEach(nodes, async node =>
        {
            List<T> messages = [];
            int messageCount = GenMessageRange.GetMessageCount();
            for (int j = 0; j < messageCount; j++)
            {
                Interlocked.Increment(ref total);
                messages.Add(new T());
            }

            await publisher.Publish(messages, TestContext.Current.CancellationToken);
        });


        while (!loop.IsCompleted)
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        return total;
    }
}