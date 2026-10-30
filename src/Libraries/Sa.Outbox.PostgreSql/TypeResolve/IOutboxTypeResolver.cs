namespace Sa.Outbox.PostgreSql.TypeResolve;

internal interface IOutboxTypeResolver
{
    /// <summary>
    /// Resolves a type name to its stored Murmur3 code. The common case is served synchronously
    /// from the loaded snapshot and allocates nothing; the insert/retry path is a cold fallback.
    /// </summary>
    ValueTask<long> GetHashCode(string typeName, CancellationToken cancellationToken);

    ValueTask<string> GetTypeName(long typeHashCode, CancellationToken cancellationToken);
}
