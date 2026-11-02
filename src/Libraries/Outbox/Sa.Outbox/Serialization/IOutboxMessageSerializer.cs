namespace Sa.Outbox.Serialization;

/// <summary>
/// The single serialization contract of the Outbox: providers store whatever these two methods
/// write and hand back what they read, so a serializer registered once is used by every provider
/// and by the publish and the delivery side alike.
/// </summary>
/// <remarks>
/// <para>
/// The default is <see cref="JsonOutboxMessageSerializer"/> (UTF-8 JSON). Replace it at the core
/// level with <c>AddSaOutbox(b =&gt; b.WithMessageSerializer(...))</c>, or per provider with the
/// provider's <c>WithMessageSerializer(...)</c>; both register the same service type, so the last
/// registration wins and the other provider follows it too.
/// </para>
/// <para>
/// Payloads are written by the publisher and read by the consumer, so a non-JSON implementation
/// must be deployed on both sides before the first message is published. Providers are not
/// interchangeable at the storage level either — switching providers does not migrate pending
/// messages.
/// </para>
/// </remarks>
public interface IOutboxMessageSerializer
{
    /// <summary>Reads a payload written by <see cref="Serialize{T}(Stream, T)"/>.</summary>
    T? Deserialize<T>(Stream stream);

    /// <summary>Writes a payload into the provider-owned stream or entry body.</summary>
    void Serialize<T>(Stream stream, T value);
}
