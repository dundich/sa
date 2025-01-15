using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace Sa.Data.Cache;


public static class Setup
{
    public static IServiceCollection AddFusionCacheEx(this IServiceCollection services, string cacheName, Action<IServiceProvider, FusionCacheEntryOptions>? configure = null)
    {
        services.AddFusionCacheSystemTextJsonSerializer();

        // https://github.com/ZiggyCreatures/FusionCache
        services
            .AddFusionCache(cacheName)
            .WithPostSetup((sp, c) =>
            {
                FusionCacheEntryOptions ops = c.DefaultEntryOptions;

                ops.Duration = TimeSpan.FromMinutes(2);
                ops.FactorySoftTimeout = TimeSpan.FromMilliseconds(100);
                ops.FailSafeMaxDuration = TimeSpan.FromHours(2);
                ops.FailSafeThrottleDuration = TimeSpan.FromSeconds(30);
                ops.Priority = CacheItemPriority.Low;
                configure?.Invoke(sp, ops);
            })
            .WithoutLogger();

        return services;
    }
}