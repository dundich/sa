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

            return new FileSystemStorage(new FileSystemStorageSettings
            {
                BasePath = options.BasePath,
                IsReadOnly = options.IsReadOnly,
                ScopeName = string.IsNullOrWhiteSpace(options.ScopeName) ? "share" : options.ScopeName,
                StorageType = options.StorageType,
            }, sp.GetService<TimeProvider>());
        });

        return services;
    }
}
