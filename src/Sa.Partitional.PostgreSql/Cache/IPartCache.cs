using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql.Cache;

internal interface IPartCache
{
    Task<bool> InCache(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default);
    Task<bool> EnsureCache(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default);
    Task RemoveCache(string tableName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every cached snapshot, for use after a bulk change — a migration that created
    /// partitions, or a cleanup that dropped them — which happened without going through
    /// <see cref="EnsureCache"/> and therefore left the cache holding a stale view.
    /// </summary>
    Task RemoveCache();
}
