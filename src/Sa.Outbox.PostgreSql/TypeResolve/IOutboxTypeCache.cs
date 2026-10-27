
namespace Sa.Outbox.PostgreSql.TypeResolve;

internal interface IOutboxTypeCache
{
    /// <summary>
    /// The stored hash, or <c>null</c> when the type is not cached.
    /// <c>0</c> is a legal Murmur3 value and must not be used as a missing sentinel.
    /// </summary>
    Task<long?> GetCode(string typeName, CancellationToken cancellationToken);

    Task<string?> GetTypeName(long code, CancellationToken cancellationToken);

    Task Reset(CancellationToken cancellationToken);
}
