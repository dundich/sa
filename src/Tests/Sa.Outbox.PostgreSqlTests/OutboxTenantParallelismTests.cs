using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Publication;
using Sa.Outbox.Support;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests;

public class OutboxTenantParallelismTests(OutboxTenantParallelismTests.Fixture fixture)
    : IClassFixture<OutboxTenantParallelismTests.Fixture>
{
    class TestMessage : IOutboxPayloadMessage
    {
        public static string PartName => "parallel_test";
        public string PayloadId { get; } = Guid.NewGuid().ToString();
        public int TenantId { get; set; }
    }

    class ParallelTestConsumer : IConsumer<TestMessage>
    {
        // Словарь для отслеживания времени обработки по тенантам
        private static readonly ConcurrentDictionary<int, List<(DateTime Start, DateTime End)>>
            ProcessingTimes = new();

        public static int TotalProcessed = 0;

        public async ValueTask Consume(
            ConsumerGroupSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            // Имитируем обработку сообщения
            await Task.Delay(1000, cancellationToken); // 1 секунда на обработку

            var endTime = DateTime.UtcNow;

            foreach (var message in messages.Span)
            {
                var tenantId = message.PartInfo.TenantId;

                ProcessingTimes.AddOrUpdate(
                    tenantId,
                    [(startTime, endTime)],
                    (key, existing) =>
                    {
                        existing.Add((startTime, endTime));
                        return existing;
                    });

                Interlocked.Increment(ref TotalProcessed);
            }
        }

        public static void Clear()
        {
            ProcessingTimes.Clear();
            TotalProcessed = 0;
        }

        public static Dictionary<int, TimeSpan> GetOverlaps()
        {
            var result = new Dictionary<int, List<TimeSpan>>();

            foreach (var kvp in ProcessingTimes)
            {
                var tenantId = kvp.Key;
                var times = kvp.Value.OrderBy(t => t.Start).ToList();
                var overlaps = new List<TimeSpan>();

                for (int i = 1; i < times.Count; i++)
                {
                    // Проверяем перекрытие с предыдущей обработкой
                    if (times[i].Start < times[i - 1].End)
                    {
                        var overlap = times[i - 1].End - times[i].Start;
                        overlaps.Add(overlap);
                    }
                }

                result[tenantId] = overlaps;
            }

            // Суммируем все перекрытия по тенантам
            return result.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Count != 0
                    ? TimeSpan.FromMilliseconds(kvp.Value.Sum(o => o.TotalMilliseconds))
                    : TimeSpan.Zero);
        }
    }

    public class Fixture : PgDataSourceFixture
    {
        public Fixture() : base()
        {
            Services
                .AddOutbox(builder => builder
                    .WithTenants((_, sp) => sp.WithTenantIds(1, 2, 3, 4, 5))
                    .WithDeliveries(deliveryBuilder => deliveryBuilder
                        .AddDeliveryScoped<ParallelTestConsumer, TestMessage>(
                            "parallel_test_group",
                            (_, settings) =>
                            {
                                settings.ScheduleSettings
                                    .WithInterval(TimeSpan.FromMilliseconds(500))
                                    .WithInitialDelay(TimeSpan.Zero);

                                settings.ConsumeSettings
                                    .WithNoBatchingWindow()
                                    .WithTenantParallelProcessing(3) // 3 Parallel
                                    .WithTenantTimeout(TimeSpan.FromSeconds(10))
                                    .WithMaxBatchSize(10);
                            })
                    )
                )
                .AddOutboxUsingPostgreSql(cfg =>
                {
                    cfg.WithDataSource(c => c.WithConnectionString(_ => ConnectionString));
                    cfg.WithOutboxSettings((_, settings) =>
                    {
                        settings.TableSettings.DatabaseSchemaName = "parallel_test";
                        settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(1);
                    });
                    cfg.WithMessageSerializer(OutboxMessageSerializer.Instance);
                });
        }
    }

    IServiceProvider ServiceProvider => fixture.ServiceProvider;

    [Fact]
    public async Task Outbox_TenantParallelism_ShouldProcessTenantsConcurrently()
    {
        // Arrange
        Console.WriteLine(fixture.ConnectionString);
        ParallelTestConsumer.Clear();

        var scheduler = ServiceProvider.GetRequiredService<IScheduler>();
        int startedSchedules = scheduler.Start(CancellationToken.None);
        Assert.True(startedSchedules >= 1);

        var publisher = ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        // Создаем по 2 сообщения для каждого из 5 тенантов
        var messages = new List<TestMessage>();
        for (int tenantId = 1; tenantId <= 5; tenantId++)
        {
            messages.Add(new TestMessage { TenantId = tenantId });
            messages.Add(new TestMessage { TenantId = tenantId });
        }

        // Act
        ulong totalPublished = await publisher.Publish(
            messages,
            TestContext.Current.CancellationToken);


        int attempts = 0;
        const int maxAttempts = 30; // 30 * 400ms = 12 секунд максимум
        while (ParallelTestConsumer.TotalProcessed < (int)totalPublished && attempts++ < maxAttempts)
        {
            await Task.Delay(400, TestContext.Current.CancellationToken);
        }

        await Task.Delay(500, TestContext.Current.CancellationToken);
        await scheduler.Stop();


        Assert.Equal((int)totalPublished, ParallelTestConsumer.TotalProcessed);


        var overlaps = ParallelTestConsumer.GetOverlaps();
        Assert.True(overlaps.Count > 0);
    }
}
