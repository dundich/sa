using InMemoryOutbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.InMemory;
using Sa.Outbox.Publication;
using Sa.Outbox.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

// The same publish → rent → consume → acknowledge pipeline as PgOutbox.ConsoleApp, with no database,
// no Docker and no connection string. Swap AddSaOutboxUsingInMemory for AddSaOutboxUsingPostgreSql and
// the consumers below keep working unchanged — that is the whole point of the plug point.
Console.WriteLine("In-memory Outbox — no database, no Docker.");

const int Expected = 4;

IHost host = Host.CreateDefaultBuilder()
    .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Information))
    .ConfigureServices(services => services
        .AddSaOutboxUsingInMemory(
            configure: settings => settings.Capacity = 1_000,
            configureOutbox: builder => builder
                .WithMessageSerializer(OutboxMessageSerializer.Instance)
                .WithTenants((_, tenants) => tenants.WithTenantIds(1, 2))
                .WithMetadata((_, metadata) => metadata.AddMetadata<SomeMessage>("some", getPayloadId: m => m.PayloadId))
                .WithDeliveries(deliveries => deliveries.AddDelivery<PrintConsumer, SomeMessage>("print", (_, consumer) => consumer
                    .WithInterval(TimeSpan.FromMilliseconds(200))
                    .WithNoBatchingWindow()
                    .StartImmediately())))
        .AddSingleton(new DeliverySignal(Expected)))
    .Build();

var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();

await publisher.Publish(
[
    new SomeMessage("01", "Hi from tenant 1"),
    new SomeMessage("02", "Hi from tenant 1 again"),
    new SomeMessage("03", "Third message"),
], tenantId: 1);

await publisher.Publish(
[
    new SomeMessage("04", "Hi from tenant 2"),
], tenantId: 2);

await host.StartAsync();

// The delivery job runs on its own schedule; wait for the consumer to acknowledge everything.
await host.Services.GetRequiredService<DeliverySignal>().Done.WaitAsync(TimeSpan.FromSeconds(20));

await host.StopAsync();

Console.WriteLine("All messages delivered. The store lives in process memory only — restart loses it.");

namespace InMemoryOutbox
{
    public sealed record SomeMessage(string PayloadId, string Message);

    /// <summary>Completes once the consumer has acknowledged <paramref name="expected"/> messages.</summary>
    public sealed class DeliverySignal(int expected)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public Task Done => _done.Task;

        public void Report(int count)
        {
            if (Interlocked.Add(ref _seen, count) >= expected)
                _done.TrySetResult();
        }
    }

    public sealed class PrintConsumer(ILogger<PrintConsumer> logger, DeliverySignal signal) : IConsumer<SomeMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<SomeMessage>> messages,
            CancellationToken cancellationToken)
        {
            foreach (IOutboxContextOperations<SomeMessage> message in messages.Span)
            {
                SampleLog.MessageConsumed(
                    logger,
                    filter.ConsumerGroupId,
                    filter.TenantId,
                    message.DeliveryInfo.TaskId,
                    message.Payload.PayloadId,
                    message.Payload.Message);

                message.Ok();
            }

            signal.Report(messages.Length);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Native AOT-friendly serializer, the same shape the PostgreSQL sample uses.</summary>
    public sealed class OutboxMessageSerializer : IOutboxMessageSerializer
    {
        public static readonly OutboxMessageSerializer Instance = new();

        public T? Deserialize<T>(Stream stream)
            => typeof(T) == typeof(SomeMessage)
                ? (T?)(object?)JsonSerializer.Deserialize(stream, SomeMessageJsonSerializerContext.Default.SomeMessage)
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
}

internal static partial class SampleLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "[{Group}] tenant {Tenant} #{TaskId}: {PayloadId} — {Message}")]
    public static partial void MessageConsumed(
        ILogger logger, string group, int tenant, long taskId, string payloadId, string message);
}
