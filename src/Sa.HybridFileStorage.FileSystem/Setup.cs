using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Provides extension methods for registering the filesystem file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the filesystem file storage provider using immutable <see cref="FileSystemStorageSettings"/>.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Immutable settings for the filesystem storage provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        FileSystemStorageSettings options)
    {
        services.AddSingleton<IFileStorage>(sp
            => new FileSystemStorage(options, sp.GetService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Registers the filesystem file storage provider using a mutable options builder with fluent configuration.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An action that receives an <see cref="IServiceProvider"/> and a <see cref="FileSystemStorageOptions"/> instance for fluent configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
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
