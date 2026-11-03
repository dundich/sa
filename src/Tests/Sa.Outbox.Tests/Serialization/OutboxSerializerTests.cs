using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Serialization;
using System.Text.Json;

namespace Sa.Outbox.Tests.Serialization;

/// <summary>
/// The single serialization strategy of the Outbox: <c>AddSaOutbox</c> registers the JSON
/// fallback, and any serializer registered earlier or through the builder replaces it. A provider
/// resolves the same service type, so the choice is not per provider.
/// </summary>
public class OutboxSerializerTests
{
    private sealed record Order(int Id, string Number);

    private sealed class MarkerSerializer : IOutboxMessageSerializer
    {
        public static MarkerSerializer Instance { get; } = new();

        public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream);

        public void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
    }

    [Fact]
    public void AddSaOutbox_WithoutSerializer_RegistersJsonFallback()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSaOutbox()
            .BuildServiceProvider();

        Assert.Same(
            JsonOutboxMessageSerializer.Instance,
            provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void SerializerRegisteredBeforeAddSaOutbox_WinsOverFallback()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxMessageSerializer>(MarkerSerializer.Instance);

        using ServiceProvider provider = services
            .AddSaOutbox()
            .BuildServiceProvider();

        Assert.Same(MarkerSerializer.Instance, provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void Builder_WithMessageSerializerInstance_ReplacesFallback()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSaOutbox(b => b.WithMessageSerializer(MarkerSerializer.Instance))
            .BuildServiceProvider();

        Assert.Same(MarkerSerializer.Instance, provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void Builder_WithMessageSerializerFactory_ReplacesFallback()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSaOutbox(b => b.WithMessageSerializer(_ => MarkerSerializer.Instance))
            .BuildServiceProvider();

        Assert.Same(MarkerSerializer.Instance, provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void Builder_WithMessageSerializerType_ResolvesFromContainer()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSaOutbox(b => b.WithMessageSerializer<MarkerSerializer>())
            .BuildServiceProvider();

        Assert.IsType<MarkerSerializer>(provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void JsonSerializer_RoundTripsThroughCodec()
    {
        Order order = new(7, "A-7");

        byte[] payload = OutboxPayloadCodec.Serialize(JsonOutboxMessageSerializer.Instance, order);
        Order? restored = OutboxPayloadCodec.Deserialize<Order>(JsonOutboxMessageSerializer.Instance, payload);

        Assert.Equal(order, restored);
    }
}
