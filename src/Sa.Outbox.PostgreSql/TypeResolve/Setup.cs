using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.PostgreSql.TypeResolve;

internal static class Setup
{
    public static IServiceCollection AddOutboxTypeResolver(this IServiceCollection services)
    {
        services.TryAddSingleton<IOutboxTypeCache, OutboxTypeCache>();
        services.TryAddSingleton<IOutboxTypeResolver, OutboxTypeResolver>();

        return services;
    }
}