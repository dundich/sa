using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Publication;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests;

public class OutboxParallelMessagingTests(OutboxParallelMessagingTests.Fixture fixture)
    : IClassFixture<OutboxParallelMessagingTests.Fixture>
{
    static class GenMessageRange
    {
        public const int Threads = 5;
        const int From = 10;
        const int To = 100;

        public static int GetMessageCount() => Random.Shared.Next(From, To);
    }


    class HasTenant
    {
        public int TenantId { get; set; } = Random.Shared.Next(1, 2);
    }


    class SomeMessage1 : HasTenant
    {
        public static string PartName => "multi_1";

        public string PayloadId { get; set; } = Guid.NewGuid().ToString();
    }

    class SomeMessage2 : HasTenant
    {
        public static string PartName => "multi_2";

        public string PayloadId { get; set; } = Guid.NewGuid().ToString();
    }

    static class CommonCounter
    {
        static int s_counter = 0;

        public static void Add(int count)
        {
            Interlocked.Add(ref s_counter, count);
        }
        public static int Counter => s_counter;
    }

    class SomeMessageConsumer1 : IConsumer<SomeMessage1>
    {
        public ValueTask Consume(
            ConsumerGroupSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<SomeMessage1>> messages,
            CancellationToken cancellationToken)
        {
            CommonCounter.Add(messages.Length);
            return ValueTask.CompletedTask;
        }
    }

    class SomeMessageConsumer2 : IConsumer<SomeMessage2>
    {
        public ValueTask Consume(
            ConsumerGroupSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<SomeMessage2>> messages,
            CancellationToken cancellationToken)
        {
            CommonCounter.Add(messages.Length);
            return ValueTask.CompletedTask;
        }
    }

    public class Fixture : PgDataSourceFixture
    {
        public Fixture() : base()
        {
            Services
                .AddSaOutbox(builder => builder
                    .WithTenants((_, b) => b.WithTenantIds(1, 2))
                    .WithMetadata((_, configure) => configure
                        .AddMetadata<SomeMessage1>(SomeMessage1.PartName)
                        .AddMetadata<SomeMessage2>(SomeMessage2.PartName))
                    .WithDeliveries(builder => builder
                        .AddDeliveryScoped<SomeMessageConsumer1, SomeMessage1>("test7_0", (_, settings) =>
                        {
                            settings.ScheduleSettings
                                .WithInterval(TimeSpan.FromMilliseconds(500))
                                .WithInitialDelay(TimeSpan.Zero);

                            settings.ConsumeSettings.WithMaxBatchSize(1024);
                        })
                        .AddDelivery<SomeMessageConsumer2, SomeMessage2>("test7_1", (_, settings) =>
                        {
                            settings.ScheduleSettings
                                .WithInterval(TimeSpan.FromMilliseconds(500))
                                .WithInitialDelay(TimeSpan.Zero);

                            settings.ConsumeSettings.WithMaxBatchSize(1024);
                        })
                    )
                    .WithPublishSettings((_, b) => b.WithMaxBatchSize(1024))
                )
                .AddSaOutboxUsingPostgreSql(cfg =>
                {
                    cfg
                        .WithDataSource(c => c.WithConnectionString(_ => ConnectionString))
                        .WithOutboxSettings((_, settings) =>
                        {
                            settings.TableSettings.DatabaseSchemaName = "parallel";
                            settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(1);
                        })
                        .WithMessageSerializer(OutboxMessageSerializer.Instance);
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

        List<Task<long>> tasks = [RunPublish<SomeMessage1>(publisher), RunPublish<SomeMessage2>(publisher)];
        await Task.WhenAll(tasks);

        long total = tasks.Select(c => c.Result)
            .DefaultIfEmpty()
            .Aggregate((t1, t2) => t1 + t2);

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
        where T : HasTenant, new()
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

            await publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);
        });


        while (!loop.IsCompleted)
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        return total;
    }
}
