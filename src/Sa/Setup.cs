using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.ObjectPool;
using Microsoft.IO;
using Sa.Classes;
using Sa.Host;
using Sa.Timing.Providers;

namespace Sa;

public static class Setup
{
    public static IServiceCollection AddSaInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<RecyclableMemoryStreamManager>();
        services.TryAddSingleton<ICurrentTimeProvider, CurrentTimeProvider>();
        services.TryAddSingleton<IInstanceIdProvider, DefaultInstanceIdProvider>();

        services.TryAddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();


        services.TryAddSingleton<IArrayPoolFactory, ArrayPoolFactory>();
        services.TryAddSingleton(typeof(IArrayPooler<>), typeof(ArrayPooler<>));
        return services;
    }
}
