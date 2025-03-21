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


namespace PgOutbox.ConsoleApp
{

    [OutboxMessage(part: "some")]
    public class SomeMessage : IOutboxPayloadMessage
    {
        public string PayloadId { get; set; } = default!;
        public int TenantId { get; set; }
    }


    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(SomeMessage))]
    public partial class SomeMessageJsonSerializerContext : JsonSerializerContext
    {
    }


    //https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
    //public class WeatherForecast
    //{
    //    public DateTime Date { get; set; }
    //    public int TemperatureCelsius { get; set; }
    //    public string? Summary { get; set; }
    //}

    //[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]
    //[JsonSerializable(typeof(WeatherForecast))]
    //internal partial class SerializeOnlyContext : JsonSerializerContext
    //{
    //}

    //[JsonSerializable(typeof(WeatherForecast), GenerationMode = JsonSourceGenerationMode.Serialization)]
    //internal partial class SerializeOnlyWeatherForecastOnlyContext : JsonSerializerContext
    //{
    //}




    public class SomeMessageConsumer : IConsumer<SomeMessage>
    {
        static int s_Counter = 0;

        public async ValueTask Consume(IReadOnlyCollection<IOutboxContext<SomeMessage>> outboxMessages, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref s_Counter, outboxMessages.Count);
            await Task.Delay(100, cancellationToken);
        }

        public static int Counter => s_Counter;
    }

    // https://stackoverflow.com/questions/78639150/use-system-text-json-converters-with-jsontypeinfo-for-aot
    // https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
    // https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-9.0
    // https://okyrylchuk.dev/blog/intro-to-serialization-with-source-generation-in-system-text-json/
    public class OutboxMessageSerializer : IOutboxMessageSerializer
    {
        public T? Deserialize<T>(Stream stream)
        {
            // return JsonSerializer.Deserialize<T>(stream);
        }

        public void Serialize<T>(Stream stream, T value)
        {
            // JsonSerializer.Serialize(stream, value);
        }
    }
}
