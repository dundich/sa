using System.Collections.Concurrent;
using Sa.Classes;
using Sa.Extensions;

namespace Sa.Partitional.PostgreSql.Cache;

internal sealed class PartCache(
    IPartRepository repository
    , ISqlBuilder sqlBuilder
    , PartCacheSettings settings
    , TimeProvider? timeProvider = null
) : IPartCache
{

    private readonly ConcurrentDictionary<string, Task<List<PartByRangeInfo>>> _cache = new();

    public async Task<bool> InCache(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
    {
        if (sqlBuilder[tableName] == null) return false;

        List<PartByRangeInfo> list = await GetPartsInCache(tableName, cancellationToken);

        if (list.Count == 0) return false;

        return list.Exists(c => partValues.SequenceEqual(c.PartValues) && c.PartBy.GetRange(c.FromDate).InRange(date));
    }

    private Task<List<PartByRangeInfo>> GetPartsInCache(string tableName, CancellationToken cancellationToken)
        => _cache.GetOrAdd(tableName, SelectPartsInDb, cancellationToken);

    // search and set the cache duration based result set
    private async Task<List<PartByRangeInfo>> SelectPartsInDb(string tableName, CancellationToken cancellationToken)
    {
        try
        {
            var tp = timeProvider ?? TimeProvider.System;

            DateTimeOffset from = (tp.GetUtcNow() - settings.CachedFromDate).StartOfDay();
            List<PartByRangeInfo> list = await repository.GetPartsFromDate(tableName, from, cancellationToken);
            return list;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }

    public async Task<bool> EnsureCache(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
    {
        bool result = await InCache(tableName, date, partValues, cancellationToken);
        if (result) return true;

        await repository.CreatePart(tableName, date, partValues, cancellationToken);

        await RemoveCache(tableName, cancellationToken);

        result = await InCache(tableName, date, partValues, cancellationToken);

        return result;
    }

    public Task RemoveCache(string tableName, CancellationToken cancellationToken = default)
        => Task.FromResult(_cache.Remove(tableName, out _));
}
