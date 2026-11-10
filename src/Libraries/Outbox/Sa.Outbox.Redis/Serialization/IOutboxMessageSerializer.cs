namespace Sa.Outbox.Redis.Serialization;

/// <summary>
/// Compatibility alias for <see cref="Sa.Outbox.Serialization.IOutboxMessageSerializer"/>, kept so
/// an existing Redis serializer implementation keeps compiling.
/// </summary>
/// <remarks>
/// Serialization is a core concern: the same serializer is used by every provider, and the Redis
/// provider now resolves <see cref="Sa.Outbox.Serialization.IOutboxMessageSerializer"/> from DI.
/// Implement the core interface in new code; this one is a subtype of it, so an implementation
/// still satisfies the provider's <c>WithMessageSerializer(...)</c>.
/// </remarks>
[Obsolete("Implement Sa.Outbox.Serialization.IOutboxMessageSerializer instead; this alias will be removed in a future release.")]
public interface IOutboxMessageSerializer : Sa.Outbox.Serialization.IOutboxMessageSerializer
{
}
