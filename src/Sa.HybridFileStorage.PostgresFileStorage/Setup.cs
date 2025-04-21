using Microsoft.Extensions.DependencyInjection;

namespace Sa.HybridFileStorage.PostgresFileStorage;


public static class Setup
{
    public static IServiceCollection AddPostgresHybridFileStorage(this IServiceCollection services, Action<IPostgresFileStorageConfiguration>? configure = null)
    {
        var configurator = new PostgresFileStorageConfiguration(services);
        configure?.Invoke(configurator);
        return services;
    }
}
