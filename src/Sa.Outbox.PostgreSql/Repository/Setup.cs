using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Repository.Plug;

namespace Sa.Outbox.PostgreSql.Repository;

internal static class Setup
{
    public static IServiceCollection AddOutboxRepositories(this IServiceCollection services)
    {
        services.AddOutboxPlugins();

        services.TryAddSingleton<IOutboxPartRepository, OutboxPartRepository>();
        services.TryAddSingleton<IOutboxMsgTypeRepository, OutboxMsgTypeRepository>();
        services.TryAddSingleton<IOutboxTaskLoader, OutboxTaskLoader>();

        return services;
    }
}
