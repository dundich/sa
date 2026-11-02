using System.Text.Json;

namespace Sa.Outbox.Redis.Serialization;

/// <summary>
/// The default <see cref="IOutboxMessageSerializer"/>: UTF-8 JSON via <see cref="JsonSerializer"/>
/// with the default options, the same wire format the PostgreSQL provider uses.
/// </summary>
/// <remarks>
/// The reflection-based <see cref="JsonSerializer"/> overloads are used deliberately — the provider
/// accepts a custom serializer for AOT deployments and for source-generated JSON contexts. The same
/// trade-off exists in <c>Sa.Outbox.PostgreSql</c>.
/// </remarks>
internal sealed class OutboxMessageSerializer : IOutboxMessageSerializer
{
    public static OutboxMessageSerializer Instance { get; } = new();

#pragma warning disable IL2026
#pragma warning disable IL3050 // Reflection-based serialization is not AOT-safe; swap the serializer if that matters.
    public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream);

    public void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
#pragma warning restore IL3050
#pragma warning restore IL2026
}
