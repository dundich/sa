using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="System.ComponentModel.DataAnnotations.ValidationException">Thrown when the settings are invalid.</exception>
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        FileSystemStorageSettings options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        // A fresh, validated instance per registration: the caller keeps a mutable handle to the
        // record it passed in, and later mutating it must not change an already-registered storage.
        var settings = options with { };

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFileStorage>(sp => new FileSystemStorage(
            settings,
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// Registers the filesystem file storage provider using a mutable options builder with fluent configuration.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An action that receives a <see cref="FileSystemStorageOptions"/> instance for fluent configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="configure"/> is <c>null</c>.</exception>
    /// <exception cref="System.ComponentModel.DataAnnotations.ValidationException">Thrown when the configured options are invalid.</exception>
    /// <remarks>
    /// The callback runs eagerly against a throwaway options instance, so the caller's closure
    /// must capture any external value it needs. Resolving host services from inside it is not
    /// possible: the registration runs before the container is built.
    /// </remarks>
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        Action<FileSystemStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Fail fast: validate at registration rather than lazily at first resolve, and leave the
        // service collection untouched when the options turn out to be invalid.
        FileSystemStorageOptions options = new();
        configure.Invoke(options);
        options.Validate();

        // ToSettings() is the only mapping between the two types, so a property added to one
        // cannot be silently dropped from the other. The previous hand-written copy listed four
        // of the five properties and left BufferSize at its default.
        return AddSaFileSystemFileStorage(services, options.ToSettings());
    }
}
