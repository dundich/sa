namespace Sa.Outbox.Redis.Poison;

/// <summary>
/// Receives messages the Redis Outbox stops delivering: a pending entry whose delivery counter
/// exceeded <c>ConsumeSettings.MaxDeliveryAttempts</c>, or a permanent outcome reported by the
/// consumer.
/// </summary>
/// <remarks>
/// <para>
/// The provider deliberately keeps no dead-letter stream of its own. The default handler
/// (<c>DropPoisonHandler</c>) logs a warning and lets the provider acknowledge the entry; replace it
/// through <c>WithPoisonHandler</c> on <c>IRedisOutboxConfiguration</c>
/// to persist the message to a store of your choice (a database, object storage, another queue) or
/// to raise an alert.
/// </para>
/// <para>
/// The handler runs <b>before</b> <c>XACK</c>. Returning normally means "the provider may
/// acknowledge"; throwing means "leave the entry pending and try again later", so the handler is
/// invoked at-least-once and must tolerate duplicates.
/// </para>
/// </remarks>
public interface IRedisOutboxPoisonHandler
{
    /// <summary>Handles one poisoned message.</summary>
    /// <param name="message">The message and the reason it was poisoned.</param>
    /// <param name="cancellationToken">The cancellation token of the delivery cycle.</param>
    ValueTask Handle(RedisOutboxPoisonMessage message, CancellationToken cancellationToken);
}
