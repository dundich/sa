namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// The stored payload of one message, exactly as the serializer wrote it.
/// </summary>
/// <remarks>
/// The bytes are returned as stored; deserialize them with the provider's
/// <see cref="Sa.Outbox.Serialization.IOutboxMessageSerializer"/> and the type named by
/// <see cref="PayloadType"/>. The tracker does not deserialize on its own: it is registered as a
/// singleton and must not assume the caller's type is available.
/// </remarks>
public sealed record PgOutboxMessagePayload
{
    /// <summary>The message id (<c>msg_id</c>, a v7 UUID).</summary>
    public required Guid MessageId { get; init; }

    /// <summary>The hash stored in <c>msg_payload_type</c>.</summary>
    public required long PayloadTypeId { get; init; }

    /// <summary>
    /// The type name from <c>__type$</c>, or <see langword="null"/> when the registry table does not
    /// exist yet or holds no row for this hash.
    /// </summary>
    public string? PayloadType { get; init; }

    /// <summary>The payload size recorded at publish time, in bytes.</summary>
    public required int Size { get; init; }

    /// <summary>The serialized payload.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }
}
