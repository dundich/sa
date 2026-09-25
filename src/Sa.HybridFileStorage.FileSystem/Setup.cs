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
        options.Validate();

        services.AddSingleton<IFileStorage>(sp
            => new FileSystemStorage(options, sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }

    /// <summary>
    /// Registers the filesystem file storage provider using a mutable options builder with fluent configuration.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An action that receives an <see cref="IServiceProvider"/> and a <see cref="FileSystemStorageOptions"/> instance for fluent configuration.
    /// The provider is a throwaway probe built from an empty service collection: it does not contain host services
    /// (such as <c>IConfiguration</c>), so capture any external values in a closure instead of resolving them from the provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        Action<IServiceProvider, FileSystemStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Fail fast: validate options at registration time rather than lazily at first resolve.
        // A throwaway provider lets the callback resolve dependencies it may need, while keeping
        // the original services collection untouched until everything is known to be valid.
        using var probe = new ServiceCollection().BuildServiceProvider();
        FileSystemStorageOptions options = new();
        configure.Invoke(probe, options);
        options.Validate();

        services.AddSingleton<IFileStorage>(sp =>
            new FileSystemStorage(new FileSystemStorageSettings
            {
                BasePath = options.BasePath,
                IsReadOnly = options.IsReadOnly,
                Basket = options.Basket,
                StorageType = options.StorageType,
            }, sp.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }
}
