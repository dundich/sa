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
IHost host = Host.CreateDefaultBuilder()
    .ConfigureServices(services => services
        .AddOutbox(builder => builder
            .WithPartitioningSupport((_, sp) => sp.WithTenantIds(1, 2, 3))
            .WithDeliveries(builder => builder
                .AddDelivery<Group1Consumer, SomeMessage>("group1", (_, settings) =>
                {
                    settings.ScheduleSettings.WithIntervalSeconds(5).WithImmediate();
                    settings.ConsumeSettings.WithSingleIteration();
                })
                .AddDelivery<RndConsumer, SomeMessage>("rnd", (_, settings) => settings
                    .ConsumeSettings
                        .WithSingleIteration().WithMaxDeliveryAttempts(2))
            )
        )
        // outbox pg
        .AddOutboxUsingPostgreSql(cfg => cfg
            .ConfigureDataSource(ds => ds.WithConnectionString(connectionString))
            .ConfigureOutboxSettings((_, settings) => settings.TableSettings.WithSchema("test"))
            .WithMessageSerializer(new OutboxMessageSerializer())
        )
        .AddHostedService<MessagePublisherService>()
    )
    .Build();

// -- code publish

var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();

await publisher.Publish([
    new SomeMessage("01", "Hi 1", 1),
    new SomeMessage("02", "Hi 2", 2),
    new SomeMessage("03", "Hi 3", 3)
]);


await host.RunAsync();


namespace PgOutbox
{
    public sealed record SomeMessage(string PayloadId, string Message, int TenantId) : IOutboxPayloadMessage
    {
        static string IOutboxHasPart.PartName => "some_msg";
    }


    public sealed class Group1Consumer(ILogger<Group1Consumer> logger) : IConsumer<SomeMessage>
    {
        public async ValueTask Consume(
            ConsumeSettings settings,
            IReadOnlyCollection<IOutboxContextOperations<SomeMessage>> outboxMessages,
            CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            Handler.Log(logger, settings, outboxMessages);
        }
    }

    public sealed class RndConsumer(ILogger<RndConsumer> logger) : IConsumer<SomeMessage>
    {
        static int s_counter = 0;

        public async ValueTask Consume(
            ConsumeSettings settings,
            IReadOnlyCollection<IOutboxContextOperations<SomeMessage>> outboxMessages,
            CancellationToken cancellationToken)
        {
            await Task.Delay(500, cancellationToken);

            if (Interlocked.Increment(ref s_counter) > 2)
            {
                settings.WithMaxProcessingIterations(100);
            }

            bool isLogged = false;

            foreach (var msg in outboxMessages.Where(c => c.PartInfo.TenantId == 3))
            {
                isLogged = true;
                switch (Random.Shared.Next(0, 6))
                {
                    case 0:
                        msg.Aborted("skip");
                        break;
                    case 1:
                        msg.Warn(new Exception("No permanent error"));
                        break;
                    case 2:
                        msg.Postpone(TimeSpan.FromMinutes(1));
                        break;
                    case 3:
                        msg.Error(new Exception("Permanent error"));
                        break;
                    default:
                        msg.Ok();
                        break;
                }
            }

            if (isLogged) Handler.Log(logger, settings, outboxMessages);
        }
    }


    static class Handler
    {
        public static void Log(
            ILogger logger,
            ConsumeSettings settings,
            params IEnumerable<IOutboxContextOperations<SomeMessage>> outboxMessages)
        {
            logger.LogWarning("======= {Group} =======", settings.ConsumerGroupId);
            foreach (var msg in outboxMessages)
            {
                logger.LogInformation("{Date}   #{TaskId}: {Payload} [{Code}]", DateTime.Now, msg.DeliveryInfo.TaskId, msg.Payload, msg.DeliveryResult.Code);
            }
        }
    }


    public class MessagePublisherService(IOutboxMessagePublisher publisher) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                for (int i = 0; i < 100 && !stoppingToken.IsCancellationRequested; i++)
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
