using Sa.Classes;

namespace Sa.Partitional.PostgreSql.Cache;

public interface IPartCache
{
    ValueTask<bool> InCache(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default);
    ValueTask<bool> EnsureCache(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default);
    ValueTask RemoveCache(string tableName, CancellationToken cancellationToken = default);
}
