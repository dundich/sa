using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgOutbox.ConsoleApp;
using Sa.Outbox;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.Support;
using System.Text.Json;
using System.Text.Json.Serialization;

Console.WriteLine("Hello, Pg Outbox!");

var connectionString = "Host=localhost;Username=service_user;Password=service_user;Database=test_1";

// default configure...
IHostBuilder builder = Host.CreateDefaultBuilder();

builder.ConfigureServices(services => services
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
                settings.ExtractSettings.MaxBatchSize = 1;
            })
        )
    )
    .AddOutboxUsingPostgreSql(cfg =>
    {
        cfg.ConfigureDataSource(c => c.WithConnectionString(_ => connectionString));
        cfg.ConfigureOutboxSettings((_, settings) =>
        {
            settings.TableSettings.DatabaseSchemaName = "test";
            settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(1);
        });
        cfg.WithMessageSerializer(sp => new OutboxMessageSerializer());
    })
);

builder.UseConsoleLifetime();

var host = builder.Build();



// -- code

var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();

var messages = new SomeMessage[]
{
    new("Hi 1"),
    new("Hi 2"),
    new("Hi 3"),
    new("Hi 4" )
};

var sent = await publisher.Publish(messages);
Console.WriteLine("sent msgs with rnd TenantId: {0}", sent);

await host.RunAsync();


Console.WriteLine("The end. Recived: {0}, Successfully: {1}", SomeMessageConsumer.Counter, SomeMessageConsumer.Counter > 0);



namespace PgOutbox.ConsoleApp
{
    public sealed record SomeMessage(string Message) : IOutboxPayloadMessage
    {
        public static string PartName => "some";

        public string PayloadId { get; init; } = Guid.NewGuid().ToString();
        public int TenantId { get; init; } = Random.Shared.Next(1, 3);
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }


    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(SomeMessage))]
    public partial class SomeMessageJsonSerializerContext : JsonSerializerContext
    {
    }

    public class SomeMessageConsumer : IConsumer<SomeMessage>
    {
        private static int s_Counter = 0;

        public async ValueTask Consume(IReadOnlyCollection<IOutboxContextOperations<SomeMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref s_Counter, outboxMessages.Count);

            foreach (var outboxMessage in outboxMessages)
            {
                Console.WriteLine("Consume [#{0}, tenant:{1}] {2}", outboxMessage.OutboxId, outboxMessage.PartInfo.TenantId, outboxMessage.Payload);
            }

            await Task.Delay(100, cancellationToken);
        }


        public static int Counter => s_Counter;
    }

    #region forAOT
    public class OutboxMessageSerializer : IOutboxMessageSerializer
    {
        public T? Deserialize<T>(Stream stream)
        {
            if (typeof(T) == typeof(SomeMessage))
            {
                SomeMessage? message = JsonSerializer.Deserialize<SomeMessage>(stream, SomeMessageJsonSerializerContext.Default.SomeMessage);
                return (T?)(object?)message;
            }

            return default;
        }

        public void Serialize<T>(Stream stream, T value)
        {
            if (typeof(T) == typeof(SomeMessage))
            {
                JsonSerializer.Serialize(stream, value!, SomeMessageJsonSerializerContext.Default.SomeMessage);
            }
        }
    }
    #endregion
}
