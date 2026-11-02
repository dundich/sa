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
/// <param name="OutboxId">The id the pipeline exposed for the message (the packed stream id).</param>
/// <param name="PayloadId">The caller-supplied business id (<c>OutboxMessage.PayloadId</c>).</param>
/// <param name="PayloadType">The full payload type name, for diagnostics.</param>
/// <param name="ConsumerGroup">The consumer group that gave up on the message.</param>
/// <param name="TenantId">The tenant the message belongs to.</param>
/// <param name="Part">The part name the message was published under.</param>
/// <param name="Attempt">
/// The failed delivery number, 1-based and including this one. A message dropped after
/// <c>MaxDeliveryAttempts</c> therefore reports the delivery counter from Redis.
/// </param>
/// <param name="StatusCode">Why the provider stopped: <c>MaximumAttemptsError</c> for poison, the consumer's permanent code otherwise.</param>
/// <param name="StatusMessage">The status text: provider message or consumer result message.</param>
/// <param name="Exception">The consumer exception for a permanent outcome; <see langword="null"/> for the delivery-counter path.</param>
/// <param name="Payload">The serialized payload, as it was written to the stream.</param>
public sealed record RedisOutboxPoisonMessage(
    Guid OutboxId,
    string PayloadId,
    string PayloadType,
    string ConsumerGroup,
    int TenantId,
    string Part,
    int Attempt,
    DeliveryStatusCode StatusCode,
    string StatusMessage,
    Exception? Exception,
    ReadOnlyMemory<byte> Payload);
