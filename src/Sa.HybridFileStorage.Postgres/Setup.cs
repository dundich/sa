using Microsoft.Extensions.DependencyInjection;

namespace Sa.HybridFileStorage.Postgres;


public static class Setup
{
    public static IServiceCollection AddSaPostgreSqlFileStorage(this IServiceCollection services, Action<IPostgresFileStorageConfiguration>? configure = null)
    {
        var configurator = new PostgresFileStorageConfiguration(services);
        configure?.Invoke(configurator);
        return services;
    }
}
