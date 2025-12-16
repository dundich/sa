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
        .AddOutbox(builder => builder
            .WithPartitioningSupport((_, sp) => sp.WithTenantIds(1, 2, 3))
            .WithDeliveries(builder => builder
                .AddDelivery<SomeConsumer, SomeMessage>("group1", (_, settings) => settings
                    .ScheduleSettings
                        .WithExecutionInterval(TimeSpan.FromMilliseconds(100))
                        .WithNoInitialDelay()
                )
                .AddDelivery<OutherConsumer, SomeMessage>("group2", (_, settings) => settings
                    .ScheduleSettings
                        .WithExecutionInterval(TimeSpan.FromSeconds(1))
                        .WithInitialDelay(TimeSpan.FromSeconds(3))
                )
            )
        )
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
    new SomeMessage(Guid.NewGuid().ToString(), "Hi 1", Random.Shared.Next(1, 4)),
    new SomeMessage(Guid.NewGuid().ToString(), "Hi 2", Random.Shared.Next(1, 4)),
    new SomeMessage(Guid.NewGuid().ToString(), "Hi 3", Random.Shared.Next(1, 4)),
    new SomeMessage(Guid.NewGuid().ToString(), "Hi 4", Random.Shared.Next(1, 4)),
    new SomeMessage(Guid.NewGuid().ToString(), "Hi 5", 1),
    new SomeMessage(Guid.NewGuid().ToString(), "Hi 6", 2),
    new SomeMessage(Guid.NewGuid().ToString(), "Hi 7", 3)
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
            logger.LogWarning("======= {TenantId} =======", outboxMessages.First().PartInfo.TenantId);

            foreach (var msg in outboxMessages)
            {
                logger.LogInformation("{Payload}", msg.Payload);
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
            logger.LogWarning("======= {TenantId} =======", outboxMessages.First().PartInfo.TenantId);

            foreach (var msg in outboxMessages)
            {
                logger.LogInformation("{Payload}", msg.Payload);
            }

            await Task.Delay(100, cancellationToken);
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
