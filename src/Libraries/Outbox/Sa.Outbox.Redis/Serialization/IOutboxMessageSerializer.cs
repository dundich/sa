namespace Sa.Outbox.Redis.Serialization;

/// <summary>
/// Serializes and deserializes Outbox payloads to and from the bytes stored in a Redis stream
/// entry. Replace the default JSON implementation with
/// <see cref="Configuration.IRedisOutboxConfiguration.WithMessageSerializer(Func{IServiceProvider, IOutboxMessageSerializer})"/>.
/// </summary>
public interface IOutboxMessageSerializer
{
    /// <summary>Reads a payload from the entry body.</summary>
    T? Deserialize<T>(Stream stream);

    /// <summary>Writes a payload into the entry body.</summary>
    void Serialize<T>(Stream stream, T value);
}
