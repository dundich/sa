using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Services.Plug;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Services;

internal static class Setup
{
    public static IServiceCollection AddOutboxRepositories(this IServiceCollection services, string scopeKey)
    {
        services.AddOutboxPlugins();

        // The outbox partitional module is keyed; its manager cannot be constructor-injected, so
        // resolve the scoped one explicitly.
        services.TryAddSingleton<IOutboxPartRepository>(sp => new OutboxPartRepository(
            sp.GetRequiredKeyedService<IPartitionManager>(scopeKey),
            sp.GetRequiredService<PgOutboxTableSettings>()));

        services.TryAddSingleton<IOutboxMsgTypeRepository, OutboxMsgTypeRepository>();
        services.TryAddSingleton<IOutboxTaskLoader, OutboxTaskLoader>();

        return services;
    }
}
