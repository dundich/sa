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
    new() { Message = "Hi 1" },
    new() { Message = "Hi 2" },
    new() { Message = "Hi 3" },
    new() { Message = "Hi 4" }
};

var id = await publisher.Publish(messages);

Console.WriteLine("sent: {0}", id);

await host.RunAsync();

Console.WriteLine("recived: {0}", SomeMessageConsumer.Counter);


namespace PgOutbox.ConsoleApp
{

    [OutboxMessage(part: "some")]
    public class SomeMessage : IOutboxPayloadMessage
    {
        public string PayloadId { get; set; } = Guid.NewGuid().ToString();
        public int TenantId { get; set; } = Random.Shared.Next(1, 3);
        public string Message { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }


    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(SomeMessage))]
    public partial class SomeMessageJsonSerializerContext : JsonSerializerContext
    {
    }

    public class SomeMessageConsumer : IConsumer<SomeMessage>
    {
        private static int s_Counter = 0;

        public async ValueTask Consume(IReadOnlyCollection<IOutboxContext<SomeMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref s_Counter, outboxMessages.Count);

            foreach (var outboxMessage in outboxMessages)
            {
                Console.WriteLine("Consume [#{0}, tenant:{1}] {2}", outboxMessage.OutboxId, outboxMessage.PartInfo.TenantId, outboxMessage.Payload.Message);
            }

            await Task.Delay(100, cancellationToken);
        }

        public static int Counter => s_Counter;
    }

    // for AOT
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
}
