using StackExchange.Redis;
using System.Buffers.Binary;
using System.Globalization;

namespace Sa.Outbox.Redis.Commands;

/// <summary>
/// Converts between a Redis stream id (<c>milliseconds-sequence</c>) and the
/// <see cref="Guid"/>-shaped <c>OutboxId</c> the Outbox contract exposes.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline identifies a message by <c>IOutboxContext.OutboxId</c> (a <see cref="Guid"/>) and
/// hands the same contexts back to <c>IOutboxDeliveryManager.ReturnDelivery</c>, where the provider
/// needs the stream id again to <c>XACK</c> it. Rather than keep a Guid → stream-id index in Redis
/// (a write on every rent and a read on every finish), the stream id <em>is</em> the id: its two
/// 64-bit halves are stored in the 16 bytes of a Guid and read back.
/// </para>
/// <para>
/// The value is therefore opaque rather than a UUID: it is stable for the message across retries
/// and useful for correlation, but it is not a version-7 UUID. Use <c>PayloadId</c> for whatever the
/// application considers the business key.
/// </para>
/// </remarks>
internal static class RedisStreamId
{
    public static Guid ToOutboxId(RedisValue streamId)
    {
        if (!TryParse(streamId, out ulong milliseconds, out ulong sequence))
            return Guid.Empty;

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], milliseconds);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], sequence);

        return new Guid(bytes);
    }

    /// <summary>Packs the stream id into the rent token the pipeline hands back on finish.</summary>
    public static Sa.Outbox.Delivery.OutboxRentToken ParseRentToken(RedisValue streamId)
        => TryParse(streamId, out ulong milliseconds, out ulong sequence)
            ? new Sa.Outbox.Delivery.OutboxRentToken((long)milliseconds, (long)sequence)
            : Sa.Outbox.Delivery.OutboxRentToken.None;

    /// <summary>Recovers the stream id from the rent token. <see cref="RedisValue.Null"/> when there is none.</summary>
    public static RedisValue ToStreamId(Sa.Outbox.Delivery.OutboxRentToken token)
        => token.IsNone
            ? RedisValue.Null
            : FormattableString.Invariant($"{token.Key}-{token.SubKey}");

    /// <summary>Parses <c>milliseconds-sequence</c>.</summary>
    public static bool TryParse(RedisValue streamId, out ulong milliseconds, out ulong sequence)
    {
        milliseconds = 0;
        sequence = 0;

        if (streamId.IsNull)
            return false;

        string? value = (string?)streamId;
        if (string.IsNullOrEmpty(value))
            return false;

        int separator = value.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
            return false;

        return ulong.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds)
            && ulong.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }
}
