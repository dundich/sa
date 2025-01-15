using ZiggyCreatures.Caching.Fusion;

namespace Sa.Outbox.Partitional;

internal class PartitionalSupportCache(IFusionCacheProvider cacheProvider, PartitionalSettings? settings = null) : IPartitionalSupportCache
{
    internal static class Env
    {
        public const string CacheName = "sa-outbox";
        public const string KeyGetTenantIds = "sa-tenant-ids";
    }

    private readonly IFusionCache _cache = cacheProvider.GetCache(Env.CacheName);


    public async ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        if (settings == null) return [];

        return await _cache.GetOrSetAsync<int[]>(
            Env.KeyGetTenantIds
            , ExtractTenantIds
            , options: null
            , token: cancellationToken);
    }

    private async Task<int[]> ExtractTenantIds(FusionCacheFactoryExecutionContext<int[]> context, CancellationToken cancellationToken)
    {
        if (settings?.GetTenantIds == null) return [];
        int[] ids = await settings.GetTenantIds(cancellationToken);
        context.Options.Duration = settings.CacheTenantIdsDuration;
        return ids;
    }
}
