using Sa.Classes;
using Sa.Extensions;
using Sa.Timing.Providers;
using ZiggyCreatures.Caching.Fusion;

namespace Sa.Partitional.PostgreSql.Cache;

internal class PartCache(
    IFusionCacheProvider cacheProvider
    , IPartRepository repository
    , ISqlBuilder sqlBuilder
    , ICurrentTimeProvider timeProvider
    , PartCacheSettings settings
) : IPartCache
{
    internal static class Env
    {
        public const string CacheName = "sa-partitional";
    }

    private readonly IFusionCache _cache = cacheProvider.GetCache(Env.CacheName);


    public async ValueTask<bool> InCache(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
    {
        if (sqlBuilder[tableName] == null) return false;

        List<PartByRangeInfo> list = await GetPartsInCache(tableName, cancellationToken);

        if (list.Count == 0) return false;

        return list.Exists(c => partValues.SequenceEqual(c.PartValues) && c.PartBy.GetRange(c.FromDate).InRange(date));
    }

    private ValueTask<List<PartByRangeInfo>> GetPartsInCache(string tableName, CancellationToken cancellationToken)
    {
        return _cache.GetOrSetAsync<List<PartByRangeInfo>>(
            tableName
            , async (ctx, t) => await SelectPartsInDb(ctx, tableName, t)
            , options: null
            , token: cancellationToken);
    }

    // search and set the cache duration based result set
    private async Task<List<PartByRangeInfo>> SelectPartsInDb(FusionCacheFactoryExecutionContext<List<PartByRangeInfo>> context, string tableName, CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset from = (timeProvider.GetUtcNow() - settings.CachedFromDate).StartOfDay();
            List<PartByRangeInfo> list = await repository.GetPartsFromDate(tableName, from, cancellationToken);
            context.Options.Duration = settings.CacheDuration;
            return list;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable)
        {
            context.Options.Duration = TimeSpan.Zero;

            return [];
        }
    }

    public async ValueTask<bool> EnsureCache(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
    {
        bool result = await InCache(tableName, date, partValues, cancellationToken);
        if (result) return true;

        await repository.CreatePart(tableName, date, partValues, cancellationToken);

        await RemoveCache(tableName, cancellationToken);

        result = await InCache(tableName, date, partValues, cancellationToken);

        return result;
    }

    public ValueTask RemoveCache(string tableName, CancellationToken cancellationToken = default)
        => _cache.RemoveAsync(tableName, null, cancellationToken);
}
