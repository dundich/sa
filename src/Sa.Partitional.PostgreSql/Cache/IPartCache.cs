using Sa.Classes;

namespace Sa.Partitional.PostgreSql.Cache;

internal interface IPartCache
{
    Task<bool> InCache(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default);
    Task<bool> EnsureCache(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default);
    Task RemoveCache(string tableName, CancellationToken cancellationToken = default);
}
