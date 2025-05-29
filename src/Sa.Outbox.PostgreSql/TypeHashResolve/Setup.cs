using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.PostgreSql.TypeHashResolve;

internal static class Setup
{
    public static IServiceCollection AddMsgTypeHashResolver(this IServiceCollection services)
    {
        services.TryAddSingleton<IMsgTypeCache, MsgTypeCache>();
        services.TryAddSingleton<IMsgTypeHashResolver, MsgTypeHashResolver>();

        return services;
    }
}