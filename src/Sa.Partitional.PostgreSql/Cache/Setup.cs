using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Partitional.PostgreSql.Cache;

internal static class Setup
{
    public static IServiceCollection AddPartCache(
        this IServiceCollection services,
        string? key = null,
        Action<IServiceProvider, PartCacheSettings>? configure = null)
    {
        // Configure delegates accumulate (exactly like the migration/cleanup settings): the
        // AddSaPartitional chain registers the cache with no configure first, so a caller's later
        // AddPartCache(cfg) still has to take effect.
        //
        // The settings registration itself stays TryAdd*: a repeated call must not replace the
        // settings instance that an already constructed PartCache (registered below as a singleton)
        // captured in its constructor. The surviving factory reads every accumulated delegate.
        if (key is null)
        {
            if (configure is not null) services.AddSingleton(configure);

            services.TryAddSingleton(sp =>
            {
                PartCacheSettings cacheSettings = new();

                foreach (var config in sp.GetServices<Action<IServiceProvider, PartCacheSettings>>())
                {
                    config(sp, cacheSettings);
                }

                return cacheSettings;
            });

            services.TryAddSingleton<IPartCache, PartCache>();
        }
        else
        {
            if (configure is not null) services.AddKeyedSingleton(key, configure);

            services.TryAddKeyedSingleton<PartCacheSettings>(key, (sp, _) =>
            {
                PartCacheSettings cacheSettings = new();

                foreach (var config in sp.GetKeyedServices<Action<IServiceProvider, PartCacheSettings>>(key))
                {
                    config(sp, cacheSettings);
                }

                return cacheSettings;
            });

            services.TryAddKeyedSingleton<IPartCache>(key, (sp, _) => new PartCache(
                sp.GetRequiredKeyedService<IPartRepository>(key),
                sp.GetRequiredKeyedService<ISqlBuilder>(key),
                sp.GetRequiredKeyedService<PartCacheSettings>(key),
                sp.GetService<TimeProvider>()));
        }

        return services;
    }
}
