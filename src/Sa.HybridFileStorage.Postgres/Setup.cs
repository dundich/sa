using Microsoft.Extensions.DependencyInjection;

namespace Sa.HybridFileStorage.Postgres;


public static class Setup
{
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        Action<IPostgresFileStorageConfiguration>? configure = null)
    {
        PostgresFileStorageConfiguration configurator = new(services);
        configure?.Invoke(configurator);
        return services;
    }
}
