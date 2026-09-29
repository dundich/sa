using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Partitional.PostgreSql.Cache;

internal static class Setup
{
    public static IServiceCollection AddPartCache(this IServiceCollection services, Action<IServiceProvider, PartCacheSettings>? configure = null)
    {
        // TryAdd*, not Add*: a repeated call must not replace the settings instance that an
        // already constructed PartCache (registered below as a singleton) captured in its
        // constructor.
        services.TryAddSingleton(sp =>
        {
            PartCacheSettings cacheSettings = new();
            configure?.Invoke(sp, cacheSettings);
            return cacheSettings;
        });

        services.TryAddSingleton<IPartCache, PartCache>();
        return services;
    }
}
