using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PgOutbox;
using Sa.Outbox;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.Support;

Console.WriteLine("Hello, Pg Outbox!");

var connectionString = "Host=localhost;Username=postgres;Password=postgres;Database=postgres";

// default configure...
IHost host = Host
    .CreateDefaultBuilder()
    .ConfigureServices(services => services
        // publicher
        .AddHostedService<MessagePublisherService>()

        // outbox
        .AddOutbox(builder => builder
            .WithPartitioningSupport((_, sp) => sp.WithTenantIds(1, 2, 3))
            .WithDeliveries(builder => builder
                .AddDelivery<SomeConsumer, SomeMessage>("group1", (_, settings) => settings
                    .ScheduleSettings
                        .WithInterval(TimeSpan.FromMilliseconds(100))
                        .WithImmediate()
                )
                .AddDelivery<OutherConsumer, SomeMessage>("group2", (_, settings) =>
                {
                    settings.ScheduleSettings
                        .WithInterval(TimeSpan.FromSeconds(10))
                        .WithInitialDelay(TimeSpan.FromSeconds(3));

                    settings.ConsumeSettings
                        .WithSingleIteration();
                })
            )
        )
        // outbox pg
        .AddOutboxUsingPostgreSql(cfg => cfg
            .ConfigureDataSource(ds => ds.WithConnectionString(connectionString))
            .ConfigureOutboxSettings((_, settings) =>
            {
                settings.TableSettings.DatabaseSchemaName = "test";
                settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(1);
            })
            .WithMessageSerializer(new OutboxMessageSerializer())
        )
    )
    .UseConsoleLifetime()
    .Build();

// -- code publish

var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();

await publisher.Publish([
    new SomeMessage("01", "Hi 1", Random.Shared.Next(1, 4)),
    new SomeMessage("02", "Hi 2", Random.Shared.Next(1, 4)),
    new SomeMessage("03", "Hi 3", Random.Shared.Next(1, 4)),

    new SomeMessage("04", "Hi 4", 1),
    new SomeMessage("05", "Hi 5", 2),
    new SomeMessage("06", "Hi 6", 3)
]);


await host.RunAsync();



namespace PgOutbox
{
    public sealed record SomeMessage(string PayloadId, string Message, int TenantId) : IOutboxPayloadMessage
    {
        static string IOutboxHasPart.PartName => "some_msg";
    }


    public sealed class SomeConsumer(ILogger<SomeConsumer> logger) : IConsumer<SomeMessage>
    {
        public async ValueTask Consume(
            ConsumeSettings settings,
            IReadOnlyCollection<IOutboxContextOperations<SomeMessage>> outboxMessages,
            CancellationToken cancellationToken)
        {
            logger.LogWarning("======= {Group} =======", settings.ConsumerGroupId);

            foreach (var msg in outboxMessages)
            {
                logger.LogInformation("#{TaskId}: {Payload}", msg.DeliveryInfo.TaskId, msg.Payload);
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    public sealed class OutherConsumer(ILogger<OutherConsumer> logger) : IConsumer<SomeMessage>
    {
        public async ValueTask Consume(
            ConsumeSettings settings,
            IReadOnlyCollection<IOutboxContextOperations<SomeMessage>> outboxMessages,
            CancellationToken cancellationToken)
        {
            logger.LogWarning("======= {Group} =======", settings.ConsumerGroupId);

            foreach (var msg in outboxMessages)
            {
                logger.LogInformation("{Payload}", msg.Payload);
            }

            await Task.Delay(100, cancellationToken);
        }
    }


    public class MessagePublisherService(IOutboxMessagePublisher publisher) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                for (int i = 0; i < 1000 && !stoppingToken.IsCancellationRequested; i++)
                {
                    var rnd = Random.Shared.Next(1, 4);
                    await Task.Delay(TimeSpan.FromSeconds(rnd), stoppingToken);
                    await publisher.Publish(new SomeMessage(i.ToString(), DateTime.Now.ToString(), rnd), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }
    }


    #region forAOT
    public class OutboxMessageSerializer : IOutboxMessageSerializer
    {
        public T? Deserialize<T>(Stream stream) => (typeof(T) == typeof(SomeMessage))
                ? (T?)(object?)JsonSerializer.Deserialize<SomeMessage>(stream, SomeMessageJsonSerializerContext.Default.SomeMessage)
                : default;

        public void Serialize<T>(Stream stream, T value)
        {
            if (typeof(T) == typeof(SomeMessage))
                JsonSerializer.Serialize(stream, value!, SomeMessageJsonSerializerContext.Default.SomeMessage);
        }
    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(SomeMessage))]
    public partial class SomeMessageJsonSerializerContext : JsonSerializerContext
    {
    }
    #endregion
}
