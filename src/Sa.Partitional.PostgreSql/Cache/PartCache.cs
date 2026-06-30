using Sa.Classes;
using Sa.Extensions;
using Sa.Partitional.PostgreSql.Classes;
using System.Collections.Concurrent;

namespace Sa.Partitional.PostgreSql.Cache;

internal sealed class PartCache(
    IPartRepository repository
    , ISqlBuilder sqlBuilder
    , PartCacheSettings settings
    , TimeProvider? timeProvider = null) : IPartCache
{

    private readonly ConcurrentDictionary<string, Task<List<PartByRangeInfo>>> _cache = new();

    public async Task<bool> InCache(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default)
    {
        if (sqlBuilder[tableName] == null) return false;

        List<PartByRangeInfo> list = await GetPartsInCache(tableName, cancellationToken).ConfigureAwait(false);

        if (list.Count == 0) return false;

        return list.Exists(c => partValues.SequenceEqual(c.PartValues) && c.PartBy.GetRange(c.FromDate).InRange(date));
    }

    private async Task<List<PartByRangeInfo>> GetPartsInCache(string tableName, CancellationToken cancellationToken)
    {
        // Check if we have a valid (non-failed) cached task.
        if (_cache.TryGetValue(tableName, out var cached) && !cached.IsFaulted)
        {
            return await cached.ConfigureAwait(false);
        }

        return await _cache.GetOrAdd(tableName, SelectPartsInDb, cancellationToken).ConfigureAwait(false);
    }

    // search and set the cache duration based result set
    private async Task<List<PartByRangeInfo>> SelectPartsInDb(string tableName, CancellationToken cancellationToken)
    {
        try
        {
            var tp = timeProvider ?? TimeProvider.System;

            DateTimeOffset from = (tp.GetUtcNow() - settings.CachedFromDate).StartOfDay();
            List<PartByRangeInfo> list = await repository.GetPartsFromDate(tableName, from, cancellationToken).ConfigureAwait(false);
            return list;
        }
        catch (Npgsql.PostgresException ex) when (PgErrorCodes.IsUndefinedTable(ex))
        {
            return [];
        }
    }

    public async Task<bool> EnsureCache(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default)
    {
        bool result = await InCache(tableName, date, partValues, cancellationToken).ConfigureAwait(false);
        if (result) return true;

        await repository.CreatePart(tableName, date, partValues, cancellationToken).ConfigureAwait(false);

        await RemoveCache(tableName, cancellationToken).ConfigureAwait(false);

        result = await InCache(tableName, date, partValues, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public Task RemoveCache(string tableName, CancellationToken cancellationToken = default)
    {
        // Remove both the cached task and any failed task to allow re-fetch on next access.
        _cache.TryRemove(tableName, out _);
        return Task.CompletedTask;
    }
}
