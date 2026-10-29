
namespace Sa.Outbox.PostgreSql.TypeResolve;

internal interface IOutboxTypeCache
{
    /// <summary>
    /// Synchronous fast path: resolves the code from the already-loaded snapshot without
    /// starting a load or allocating a task. Returns <see langword="false"/> when the snapshot
    /// is not ready yet, so the caller can fall back to <see cref="GetCode"/>.
    /// </summary>
    bool TryGetCode(string typeName, out long code);

    /// <summary>
    /// Synchronous fast path: resolves the name from the already-loaded snapshot without
    /// starting a load or allocating a task. Returns <see langword="false"/> when the snapshot
    /// is not ready or the code is not in it.
    /// </summary>
    bool TryGetTypeName(long code, out string? typeName);

    /// <summary>
    /// The stored hash, or <c>null</c> when the type is not cached.
    /// <c>0</c> is a legal Murmur3 value and must not be used as a missing sentinel.
    /// </summary>
    Task<long?> GetCode(string typeName, CancellationToken cancellationToken);

    Task<string?> GetTypeName(long code, CancellationToken cancellationToken);

    Task Reset(CancellationToken cancellationToken);
}
