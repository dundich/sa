using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.TypeResolve;

internal static class Setup
{
    public static IServiceCollection AddOutboxTypeResolver(this IServiceCollection services, string providerKey)
    {
        services.TryAddKeyedSingleton<IOutboxTypeCache>(providerKey, (sp, _) =>
            new OutboxTypeCache(sp.GetRequiredKeyedService<IOutboxMsgTypeRepository>(providerKey)));

        services.TryAddKeyedSingleton<IOutboxTypeResolver>(providerKey, (sp, _) =>
            new OutboxTypeResolver(
                sp.GetRequiredKeyedService<IOutboxTypeCache>(providerKey),
                sp.GetRequiredKeyedService<IOutboxMsgTypeRepository>(providerKey)));

        return services;
    }
}
