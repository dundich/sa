using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.Cache;

namespace Sa.Outbox.PostgreSql.TypeHashResolve;

internal static class Setup
{
    public static IServiceCollection AddMsgTypeHashResolver(this IServiceCollection services)
    {
        services.AddFusionCacheEx(MsgTypeCache.Env.CacheName, (sp, opts) =>
        {
            PgOutboxCacheSettings cacheSettings = sp.GetRequiredService<PgOutboxCacheSettings>();
            opts.Duration = cacheSettings.CacheTypeDuration;
        });


        services.TryAddSingleton<IMsgTypeCache, MsgTypeCache>();
        services.TryAddSingleton<IMsgTypeHashResolver, MsgTypeHashResolver>();

        return services;
    }
}