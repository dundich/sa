using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public static class Setup
{
    public static IServiceCollection AddHybridStorage(this IServiceCollection services, Action<IServiceProvider, IHybridFileStorageBuilder>? configure = null)
    {
        services.AddSaInfrastructure();
        services.TryAddSingleton<IHybridFileStorageBuilder>(sp =>
        {
            var builder = new HybridFileStorageBuilder();
            var storages = sp.GetServices<IFileStorage>();

            foreach (IFileStorage storage in storages)
                builder.AddStorage(storage);

            configure?.Invoke(sp, builder);
            return builder;
        });

        services.TryAddSingleton<IHybridFileStorage>(sp => sp.GetRequiredService<IHybridFileStorageBuilder>().Build());

        return services;
    }
}
