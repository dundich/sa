namespace Sa.Outbox.PostgreSql.Serialization;

/// <summary>
/// Compatibility alias for <see cref="Sa.Outbox.Serialization.IOutboxMessageSerializer"/>, kept so
/// an existing PostgreSQL serializer implementation keeps compiling.
/// </summary>
/// <remarks>
/// Serialization is a core concern: the same serializer is used by every provider, and the
/// PostgreSQL provider now resolves <see cref="Sa.Outbox.Serialization.IOutboxMessageSerializer"/>
/// from DI. Implement the core interface in new code; this one is a subtype of it, so an
/// implementation still satisfies the provider's <c>WithMessageSerializer(...)</c>.
/// </remarks>
public interface IOutboxMessageSerializer : Sa.Outbox.Serialization.IOutboxMessageSerializer
{
}
