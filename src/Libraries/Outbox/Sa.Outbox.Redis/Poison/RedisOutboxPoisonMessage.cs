using Sa.Outbox.Delivery;

namespace Sa.Outbox.Redis.Poison;

/// <summary>
/// A message the provider stopped delivering: either its pending delivery counter exceeded the
/// provider limit, or the consumer reported a permanent outcome.
/// </summary>
/// <remarks>
/// <para>
/// The provider no longer writes a dead-letter stream. It hands the message to
/// <see cref="IRedisOutboxPoisonHandler"/> and, if the handler returns normally, acknowledges the
/// entry (<c>XACK</c>). The entry body remains in the stream until retention trims it, so it can
/// still be inspected with <c>XRANGE</c> — but it no longer blocks the group.
/// </para>
/// <para>
/// If the handler throws, the entry is <b>not</b> acknowledged: it stays pending and will be
/// claimed again, so handling is at-least-once, exactly like delivery.
/// </para>
/// </remarks>
public sealed record RedisOutboxPoisonMessage
{
    /// <summary>OutboxId.</summary>
    public Guid OutboxId { get; init; }
    /// <summary>PayloadId.</summary>
    public required string PayloadId { get; init; }
    /// <summary>PayloadType.</summary>
    public required string PayloadType { get; init; }
    /// <summary>ConsumerGroup.</summary>
    public required string ConsumerGroup { get; init; }
    /// <summary>TenantId.</summary>
    public int TenantId { get; init; }
    /// <summary>Part.</summary>
    public required string Part { get; init; }
    /// <summary>Attempt.</summary>
    public int Attempt { get; init; }
    /// <summary>StatusCode.</summary>
    public DeliveryStatusCode StatusCode { get; init; }
    /// <summary>StatusMessage.</summary>
    public required string StatusMessage { get; init; }
    /// <summary>Exception.</summary>
    public Exception? Exception { get; init; }
    /// <summary>Payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
}
