using Microsoft.IO;

namespace Sa.Outbox.Serialization;

/// <summary>
/// The byte bridge between <see cref="IOutboxMessageSerializer"/> and a provider that stores a
/// payload as a self-contained byte array (a Redis stream entry, a broker message, a BLOB column).
/// </summary>
/// <remarks>
/// Serialization goes through a pooled <see cref="RecyclableMemoryStream"/> and is then copied into
/// an exact-size array: the array is what the provider hands to its transport, so it must not be a
/// slice of a pooled buffer that another thread may reuse in the meantime. A provider that streams
/// the payload straight into its own writer (the PostgreSQL <c>COPY</c> path) should do that itself
/// instead of paying this copy.
/// </remarks>
public static class OutboxPayloadCodec
{
    private static readonly RecyclableMemoryStreamManager StreamManager = new();

    /// <summary>Serializes <paramref name="value"/> into a new exact-size byte array.</summary>
    public static byte[] Serialize<T>(IOutboxMessageSerializer serializer, T value)
    {
        using MemoryStream stream = StreamManager.GetStream();
        serializer.Serialize(stream, value);
        return stream.ToArray();
    }

    /// <summary>Deserializes a payload previously produced by <see cref="Serialize{T}"/>.</summary>
    public static T? Deserialize<T>(IOutboxMessageSerializer serializer, byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        return serializer.Deserialize<T>(stream);
    }
}
