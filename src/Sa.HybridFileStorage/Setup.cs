using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public static class Setup
{
    public static IServiceCollection AddSaHybridFileStorage(
        this IServiceCollection services,
        Action<IHybridFileStorageConfiguration>? configure = null)
    {
        HybridStorageBuilder builder = new(services);
        configure?.Invoke(builder);
        builder.Build();
        return services;
    }


    public static IServiceCollection AddSaInMemoryFileStorage(
        this IServiceCollection services,
        InMemoryFileStorageOptions? options = null)
    {
        options ??= new(string.Empty);

        services.AddSingleton<IFileStorage, InMemoryFileStorage>(
            sp => new InMemoryFileStorage(options, sp.GetService<TimeProvider>()));
        return services;
    }
}
