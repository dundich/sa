using System.Text.Json;

namespace Sa.Outbox.Serialization;

/// <summary>
/// The default <see cref="IOutboxMessageSerializer"/>: UTF-8 JSON via <see cref="JsonSerializer"/>
/// with the default options. Registered by <c>AddSaOutbox</c> as a fallback, so a provider uses it
/// unless the application supplies its own serializer.
/// </summary>
/// <remarks>
/// The reflection-based <see cref="JsonSerializer"/> overloads are used deliberately: this default
/// works for any message type without registration. A Native AOT deployment that cannot tolerate
/// the reflection (or needs a non-JSON wire format, custom options or a source-generated
/// <c>JsonSerializerContext</c>) replaces it with
/// <c>WithMessageSerializer(...)</c> — see <see cref="IOutboxMessageSerializer"/>.
/// </remarks>
public sealed class JsonOutboxMessageSerializer : IOutboxMessageSerializer
{
    /// <summary>The shared stateless instance.</summary>
    public static JsonOutboxMessageSerializer Instance { get; } = new();

#pragma warning disable IL2026
#pragma warning disable IL3050 // Reflection-based JSON is not AOT-safe; replace the serializer if that matters.
    /// <inheritdoc />
    public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream);

    /// <inheritdoc />
    public void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
#pragma warning restore IL3050
#pragma warning restore IL2026
}
