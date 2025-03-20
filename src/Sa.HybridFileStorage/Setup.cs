using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public static class Setup
{
    public static IServiceCollection AddHybridStorage(this IServiceCollection services, Action<IServiceProvider, IHybridFileStorageBuilder>? configure = null)
    {

        services.TryAddSingleton<IHybridFileStorageBuilder>(sp =>
        {
            var builder = new HybridFileStorageBuilder();
            configure?.Invoke(sp, builder);
            return builder;
        });

        services.TryAddSingleton<IHybridFileStorage>(sp => sp.GetRequiredService<IHybridFileStorageBuilder>().Build());

        return services;
    }
}
