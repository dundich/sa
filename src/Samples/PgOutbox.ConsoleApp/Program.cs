using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PgOutbox;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.Publication;
using System.Text.Json;
using System.Text.Json.Serialization;

Console.WriteLine("Hello, Pg Outbox!");

var connectionString = "Host=localhost;Username=postgres;Password=postgres;Database=postgres";

// default configure...
IHost host = Host.CreateDefaultBuilder().ConfigureServices(services => services
    // outbox
    .AddOutbox(builder => builder
        .WithTenants((_, t) => t.WithTenantIds(1, 2, 3))
        .WithMetadata((_, b) => b.AddMetadata<SomeMessage>("some", getPayloadId: p => p.PayloadId))
        .WithDeliveries(b => b
            .AddDeliveryScoped<Group1Consumer, SomeMessage>((_, settings) =>
            {
                settings.ScheduleSettings.WithIntervalSeconds(5).WithImmediate();
                settings.ConsumeSettings.WithSingleIteration();
            })
            .AddDelivery<RndConsumer, SomeMessage>("rnd", (_, settings) =>
            {
                settings.ScheduleSettings.WithIntervalSeconds(25);
                settings.ConsumeSettings.WithSingleIteration().WithMaxDeliveryAttempts(2);
            })
        )
    )
    // outbox for pg
    .AddOutboxUsingPostgreSql(cfg => cfg
        .WithDataSource(ds => ds.WithConnectionString(connectionString))
        .WithOutboxSettings((_, settings) =>
        {
            settings.TableSettings.WithSchema("test");
            settings.ConsumeSettings.WithMinOffset<Group1Consumer>(DateTimeOffset.Now);
        })
        .WithMessageSerializer(OutboxMessageSerializer.Instance)
    )
    // publish as service
    .AddHostedService<MessagePublisherService>()
)
.Build();

// -- code publish

var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();

await publisher.Publish([
    new SomeMessage("01", "Hi 1"),
    new SomeMessage("02", "Hi 2"),
    new SomeMessage("03", "Hi 3")
], tenantId: 1);


await host.RunAsync();


namespace PgOutbox
{
    public sealed record SomeMessage(string PayloadId, string Message);


    public sealed class Group1Consumer(ILogger<Group1Consumer> logger) : IConsumer<SomeMessage>
    {
        public async ValueTask Consume(
            ConsumerGroupSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<SomeMessage>> messages,
            CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            Monitor.Log(logger, filter, messages.Span);
        }
    }

    public sealed class RndConsumer(ILogger<RndConsumer> logger) : IConsumer<SomeMessage>
    {
        static int s_counter = 0;

        public async ValueTask Consume(
            ConsumerGroupSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<SomeMessage>> messages,
            CancellationToken cancellationToken)
        {
            await Task.Delay(500, cancellationToken);

            if (Interlocked.Increment(ref s_counter) > 2)
            {
                settings.ConsumeSettings.WithMaxProcessingIterations(100);
            }

            foreach (var msg in messages.Span)
            {
                Handle(msg);
            }

            Monitor.Log(logger, filter, messages.Span);
        }

        private static void Handle(IOutboxContextOperations<SomeMessage> msg)
        {
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
                    msg.Retry(TimeSpan.FromSeconds(30), "retry");
                    break;
                case 4:
                    msg.Error(new Exception("Permanent error"));
                    break;
                default:
                    msg.Ok();
                    break;
            }
        }
    }


    static class Monitor
    {
        public static void Log(
            ILogger logger,
            OutboxMessageFilter filter,
            ReadOnlySpan<IOutboxContextOperations<SomeMessage>> messages)
        {
            logger.LogWarning("======= {Group} : {Tenant} =======", filter.ConsumerGroupId, filter.TenantId);
            foreach (var msg in messages)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("{Date}   #{TaskId}: {Payload} [{Code}]"
                    , msg.GetUtcNow()
                    , msg.DeliveryInfo.TaskId
                    , msg.Payload
                    , msg.DeliveryResult.Code);
                }
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
                    var tenantId = Random.Shared.Next(1, 4);
                    await Task.Delay(TimeSpan.FromSeconds(tenantId), stoppingToken);

                    await publisher.PublishSingle(
                        new SomeMessage(
                            i.ToString(),
                            DateTime.Now.ToString()),
                        tenantId,
                        stoppingToken);
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
        public T? Deserialize<T>(Stream stream)
        {
            return typeof(T) switch
            {
                Type t when t == typeof(SomeMessage) =>
                    (T?)(object?)JsonSerializer.Deserialize<SomeMessage>(
                        stream, SomeMessageJsonSerializerContext.Default.SomeMessage),

                _ => default
            };
        }

        public void Serialize<T>(Stream stream, T value)
        {
            if (typeof(T) == typeof(SomeMessage))
                JsonSerializer.Serialize(stream, value!, SomeMessageJsonSerializerContext.Default.SomeMessage);
        }

        public readonly static OutboxMessageSerializer Instance = new();
    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(SomeMessage))]
    public partial class SomeMessageJsonSerializerContext : JsonSerializerContext
    {
    }
    #endregion
}
