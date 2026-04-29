using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.FileSystem;


public static class Setup
{
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        FileSystemStorageSettings options)
    {
        services.AddSingleton<IFileStorage>(sp
            => new FileSystemStorage(options, sp.GetService<TimeProvider>()));
        return services;
    }

    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        Action<IServiceProvider, FileSystemStorageOptions> configure)
    {
        services.AddSingleton<IFileStorage>(sp =>
        {
            FileSystemStorageOptions options = new();
            configure.Invoke(sp, options);
            options.Validate();

            return new FileSystemStorage(new FileSystemStorageSettings
            {
                BasePath = options.BasePath,
                IsReadOnly = options.IsReadOnly,
                Basket = options.Basket,
                StorageType = options.StorageType,
            }, sp.GetService<TimeProvider>());
        });

        return services;
    }
}
