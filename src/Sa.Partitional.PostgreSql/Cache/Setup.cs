using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Sa.Data.Cache;

namespace Sa.Partitional.PostgreSql.Cache;


internal static class Setup
{
    public static IServiceCollection AddPartCache(this IServiceCollection services, Action<IServiceProvider, PartCacheSettings>? configure = null)
    {

        services.AddTransient<PartCacheSettings>();

        services.AddFusionCacheEx(PartCache.Env.CacheName, (sp, opts) =>
        {
            PartCacheSettings cacheSettings = sp.GetRequiredService<PartCacheSettings>();
            configure?.Invoke(sp, cacheSettings);
            opts.Duration = cacheSettings.CacheDuration;
        });

        services.TryAddSingleton<IPartCache, PartCache>();
        return services;
    }
}
