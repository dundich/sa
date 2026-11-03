using Microsoft.Extensions.DependencyInjection;

namespace Sa.Data.TempFolder.FilePathResolver;

/// <summary>
/// Provides extension methods for registering file-path resolvers with the .NET dependency
/// injection container — the same keyed shape as Sa.Data.TempFolder's <c>AddSaTempFolder</c>:
/// one host, several named roots, each resolved by its own key.
/// </summary>
public static class Setup
{
    /// <summary>The registration key used by the overload that does not take a name.</summary>
    public const string DefaultName = "default";

    /// <summary>
    /// Registers a file-path resolver under an explicit key, so one host can serve several
    /// configured roots with different maps, extensions and search options.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="name">
    /// The keyed-service key the resolver is resolved by:
    /// <c>sp.GetRequiredKeyedService&lt;IFilePathResolver&gt;(name)</c>. Must be unique per registration.
    /// </param>
    /// <param name="configuredPath">The root directory every lookup is relative to.</param>
    /// <param name="configure">
    /// Optional tweaks applied once, when the instance is built — the same fluent chain as
    /// direct construction: <c>r =&gt; r.WithMap(...).WithPossibleExtensions(...).WithDeepSearch(true)</c>.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="configuredPath"/> is null or whitespace.
    /// </exception>
    /// <exception cref="InvalidOperationException">A resolver with the same name is already registered.</exception>
    /// <remarks>
    /// <para>
    /// Registration stays lazy: the instance is built on first resolve, so a root that does not
    /// exist surfaces as <see cref="DirectoryNotFoundException"/> there — never at registration.
    /// </para>
    /// <para>
    /// The file-system seam resolves keyed by this registration's name first, then a plain
    /// <see cref="IFileSystemService"/>, and falls back to the disk-backed default — swap it per
    /// registration (or globally) without touching the resolver itself.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFilePathResolver(
        this IServiceCollection services,
        string name,
        string configuredPath,
        Action<FilePathResolver>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A registration name is required.", nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        if (services.Any(d => d.ServiceType == typeof(IFilePathResolver) && Equals(d.ServiceKey, name)))
        {
            throw new InvalidOperationException(
                $"A file path resolver named '{name}' is already registered in this service collection. " +
                "Registration names must be unique — pick another name for the second registration.");
        }

        var registrationName = name;
        var registrationPath = configuredPath;
        var registrationConfigure = configure;

        services.AddKeyedSingleton<IFilePathResolver>(name, (provider, _) =>
        {
            var fileSystem = provider.GetKeyedService<IFileSystemService>(registrationName)
                ?? provider.GetService<IFileSystemService>()
                ?? new DefaultFileSystemService();

            var resolver = new FilePathResolver(fileSystem, registrationPath);
            registrationConfigure?.Invoke(resolver);
            return resolver;
        });

        return services;
    }

    /// <summary>
    /// Registers a file-path resolver under the <see cref="DefaultName"/> key:
    /// <c>sp.GetRequiredKeyedService&lt;IFilePathResolver&gt;(Setup.DefaultName)</c> (a bare
    /// <c>GetRequiredService&lt;IFilePathResolver&gt;()</c> does not resolve keyed services).
    /// </summary>
    /// <inheritdoc cref="AddFilePathResolver(IServiceCollection, string, string, Action{FilePathResolver}?)" path="/param[@name='services'] | /param[@name='configuredPath'] | /param[@name='configure'] | /returns | /exception | /remarks" />
    public static IServiceCollection AddFilePathResolver(
        this IServiceCollection services,
        string configuredPath,
        Action<FilePathResolver>? configure = null)
        => services.AddFilePathResolver(DefaultName, configuredPath, configure);
}
