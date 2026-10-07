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
    /// The configuration channel: the section via <see cref="IFileSystemStorageBuilder.FromConfiguration"/>
    /// and the standard pipeline (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) via
    /// <see cref="IFileSystemStorageBuilder.Options"/>, in the same delegate. Invoked once, immediately;
    /// its <c>Options(...)</c> actions are replayed after this method's own registrations, so their
    /// <c>Configure</c> runs last and their <c>Validate</c> adds to — rather than replaces — the
    /// built-in checks.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a filesystem storage was already registered in this collection.</exception>
    /// <remarks>
    /// The options pipeline runs in a fixed order: the section binding (<c>FromConfiguration</c>,
    /// raw values) and then the caller's <c>Options(...)</c> <c>Configure</c> calls →
    /// <c>PostConfigure</c> (this method's normalisation) and then the caller's
    /// <c>Options(...)</c> <c>PostConfigure</c> calls → validation. Validation therefore sees the
    /// normalised <c>BasePath</c>, and <c>ValidateOnStart()</c> turns an invalid configuration into
    /// an <see cref="OptionsValidationException"/> at host start instead of a failed first upload.
    /// </remarks>
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        Action<IFileSystemStorageBuilder>? configure = null)
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

        FileSystemStorageBuilder? storageBuilder = null;

        if (configure is not null)
        {
            storageBuilder = new FileSystemStorageBuilder();
            configure(storageBuilder);
        }

        // Fixed slot: the section binds after the callback has recorded it, before its
        // Options(...) actions replay — wherever those calls sit in the callback.
        var sectionPath = storageBuilder?.ConfigSectionPath;

        if (sectionPath is not null)
        {
            builder.BindConfiguration(sectionPath);
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

        // Replayed in this slot — after the section binding and after this method's own
        // PostConfigure/Validate — so the caller's Configure beats the section and its
        // Validate runs after ours.
        if (storageBuilder is { SettingsActions.Count: > 0 })
        {
            foreach (var settingsAction in storageBuilder.SettingsActions)
            {
                settingsAction(builder);
            }
        }

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