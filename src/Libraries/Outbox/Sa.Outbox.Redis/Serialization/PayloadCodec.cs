using Microsoft.IO;

namespace Sa.Outbox.Redis.Serialization;

/// <summary>
/// The byte bridge between <see cref="IOutboxMessageSerializer"/> and a Redis stream entry.
/// </summary>
/// <remarks>
/// Serialization goes through a pooled <see cref="RecyclableMemoryStream"/> and is then copied into
/// an exact-size array: the array is what a Redis command holds until it has been written, so it
/// must not be a slice of a pooled buffer that another thread may reuse in the meantime.
/// </remarks>
internal static class PayloadCodec
{
    private static readonly RecyclableMemoryStreamManager StreamManager = new();

    public static byte[] Serialize<T>(IOutboxMessageSerializer serializer, T value)
    {
        using MemoryStream stream = StreamManager.GetStream();
        serializer.Serialize(stream, value);
        return stream.ToArray();
    }

    public static T? Deserialize<T>(IOutboxMessageSerializer serializer, byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        return serializer.Deserialize<T>(stream);
    }
}
