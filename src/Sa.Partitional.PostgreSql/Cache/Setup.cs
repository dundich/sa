using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Partitional.PostgreSql.Cache;

internal static class Setup
{
    public static IServiceCollection AddPartCache(this IServiceCollection services, Action<IServiceProvider, PartCacheSettings>? configure = null)
    {
        services.AddTransient<PartCacheSettings>(sp =>
        {
            PartCacheSettings cacheSettings = new();
            configure?.Invoke(sp, cacheSettings);
            return cacheSettings;
        });

        services.TryAddSingleton<IPartCache, PartCache>();
        return services;
    }
}
