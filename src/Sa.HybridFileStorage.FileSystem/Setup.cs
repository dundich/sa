using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Provides extension methods for registering the filesystem file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the filesystem file storage provider using the standard options pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">
    /// An optional callback receiving the <see cref="OptionsBuilder{TOptions}"/> for this provider, so
    /// configuration goes through the standard <c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>
    /// methods rather than a bespoke overload.
    /// </param>
    /// <param name="configSectionPath">
    /// An optional configuration section to bind the options from, e.g. <c>"FileSystemStorage"</c>.
    /// Bound first, so a <c>Configure</c> call in <paramref name="configure"/> has the last word.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a filesystem storage was already registered in this collection.</exception>
    /// <remarks>
    /// The options pipeline runs in a fixed order: <c>Configure</c> (pre-initialisation, raw values) →
    /// <c>PostConfigure</c> (this method's normalisation) → <c>PostConfigure</c> calls made in
    /// <paramref name="configure"/> → validation. Validation therefore sees the normalised
    /// <c>BasePath</c>, and <c>ValidateOnStart()</c> turns an invalid configuration into an
    /// <see cref="OptionsValidationException"/> at host start instead of a failed first upload.
    /// <para>
    /// The callback is invoked after this method's own registrations, so its <c>Configure</c> runs last
    /// and its <c>Validate</c> adds to — rather than replaces — the built-in checks.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        Action<OptionsBuilder<FileSystemStorageOptions>>? configure = null,
        string? configSectionPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The provider owns the unnamed FileSystemStorageOptions instance. A second call would add a
        // second IFileStorage built from those same options, and its Configure callback would stack
        // on top of the first one's — one storage with a mangled BasePath plus a copy of it. Fail
        // here, where the cause is visible, mirroring AddSaS3FileStorage.
        if (services.Any(d => d.ServiceType == typeof(FileSystemStorageRegistration)))
        {
            throw new InvalidOperationException(
                "AddSaFileSystemFileStorage has already been registered in this service collection. " +
                "The second call would register another IFileStorage over the same options instance, " +
                "and both Configure callbacks would apply, so the storage would silently use merged " +
                "settings. Register only one filesystem storage per service collection.");
        }

        services.AddSingleton(new FileSystemStorageRegistration());

        var builder = services.AddOptions<FileSystemStorageOptions>();

        if (configSectionPath is not null)
        {
            builder.BindConfiguration(configSectionPath);
        }

        // Post-configuration: resolve the base path once, here, instead of twice in the storage's field
        // initialisers, and trim the names so validation and the file-ID scheme see clean values.
        // Runs before validation, so the checks below apply to the normalised result.
        builder.PostConfigure(static options =>
        {
            options.StorageType = options.StorageType.Trim();
            options.Basket = options.Basket.Trim();

            // Guarded on purpose. Path.GetFullPath("   ") succeeds on Unix — spaces are legal in a
            // filename — so normalising unconditionally would turn a blank BasePath into a directory
            // literally named "   " and validation would never see the mistake. Leaving a blank value
            // untouched lets Validate() report it.
            if (!string.IsNullOrWhiteSpace(options.BasePath))
            {
                options.BasePath = Path.GetFullPath(options.BasePath.Trim());
            }
        });

        builder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FileSystemStorageOptions>, FileSystemStorageOptionsValidator>());

        // Invoked last, so the caller's Configure runs after any section binding and its PostConfigure
        // and Validate run after this method's own.
        configure?.Invoke(builder);

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IFileStorage>(sp => new FileSystemStorage(
            sp.GetRequiredService<IOptions<FileSystemStorageOptions>>().Value,
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}

/// <summary>
/// Sentinel marker recording that the filesystem provider is already registered in this collection,
/// so a second <see cref="Setup.AddSaFileSystemFileStorage"/> call fails fast instead of silently
/// stacking a second <see cref="IFileStorage"/> over the same options instance.
/// </summary>
internal sealed class FileSystemStorageRegistration
{
}