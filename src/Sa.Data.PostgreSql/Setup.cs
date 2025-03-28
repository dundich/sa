using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql.Configuration;

namespace Sa.Data.PostgreSql;

public static class Setup
{
    public static IServiceCollection AddPgDataSource(this IServiceCollection services, Action<IPgDataSourceSettingsBuilder>? configure = null)
    {
        PgDataSourceSettingsBuilder builder = new(services);
        configure?.Invoke(builder);
        services.TryAddSingleton<IPgDataSource, PgDataSource>();
        services.TryAddSingleton<IPgDistributedLock, PgDistributedLock>();
        return services;
    }
}
