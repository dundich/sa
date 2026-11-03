namespace Sa.Outbox.Delivery;

/// <summary>
/// Provider-owned identity of one rented delivery: the value a provider needs to return the message
/// to the right storage row without re-deriving it from the public message id.
/// </summary>
/// <remarks>
/// <para>
/// Two 64-bit values are exactly what a relational or stream storage can address a row by: the
/// PostgreSQL provider uses <c>(task id, delivery id)</c>, the Redis provider uses the stream-id
/// halves <c>(milliseconds, sequence)</c>. The token is opaque to the core — it is handed back to the
/// provider unchanged in <c>ReturnDelivery</c>/<c>ExtendDelivery</c> — and allocation-free, because it
/// is a readonly struct carried inside <see cref="OutboxDeliveryMessage{TMessage}"/>.
/// </para>
/// <para>
/// Replaces the previous convention of encoding the rent identity into <see cref="IOutboxContext.OutboxId"/>
/// (the Redis provider did that by packing the stream id into a <see cref="Guid"/>) or of re-reading it
/// from <see cref="OutboxTaskDeliveryInfo"/> (the PostgreSQL provider).
/// </para>
/// </remarks>
/// <param name="Key">The primary part of the identity (a task id, a stream-id millisecond part).</param>
/// <param name="SubKey">The secondary part (a delivery id, a stream-id sequence).</param>
public readonly record struct OutboxRentToken(long Key, long SubKey = 0)
{
    /// <summary>The "no token" value; see <see cref="IsNone"/>.</summary>
    public static OutboxRentToken None => default;

    /// <summary>Whether the provider supplied no token for this rent.</summary>
    public bool IsNone => Key == 0 && SubKey == 0;

    /// <inheritdoc/>
    public override string ToString() => IsNone ? "none" : $"{Key}-{SubKey}";
}
