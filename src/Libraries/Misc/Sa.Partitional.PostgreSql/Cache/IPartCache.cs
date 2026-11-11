namespace Sa.Partitional.PostgreSql.Cache;

internal interface IPartCache
{
    Task<bool> InCacheAsync(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default);
    Task EnsureCacheAsync(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default);
    Task RemoveCacheAsync(string tableName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every cached snapshot, for use after a bulk change — a migration that created
    /// partitions, or a cleanup that dropped them — which happened without going through
    /// <see cref="EnsureCacheAsync"/> and therefore left the cache holding a stale view.
    /// </summary>
    Task RemoveCacheAsync();
}
