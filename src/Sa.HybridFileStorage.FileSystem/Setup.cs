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

        // One filesystem storage per collection until the multi-instance stage formalises several:
        // today a second call would register a second IFileStorage over its own options instance and
        // silently change the chain every basket probes. Fail here, where the cause is visible,
        // mirroring AddSaS3FileStorage.
        if (services.Any(d => d.ServiceType == typeof(FileSystemStorageRegistration)))
        {
            throw new InvalidOperationException(
                "AddSaFileSystemFileStorage has already been registered in this service collection. " +
                "A second filesystem storage in one collection is not supported yet — register only " +
                "one, or configure the shared storage's basket per operation.");
        }

        FileSystemStorageBuilder? storageBuilder = null;

        if (configure is not null)
        {
            storageBuilder = new FileSystemStorageBuilder();
            configure(storageBuilder);
        }

        // Fixed slot: the section binds after the callback has recorded it, before its
        // Options(...) actions replay — wherever those calls sit in the callback.
        var sectionPath = storageBuilder?.ConfigSectionPath;

        // One registration, one named options instance — the name is unique per registration, not
        // derived from anything the caller passes, so two calls never stack their Configure
        // actions on one shared instance. The factory below resolves it through IOptionsMonitor,
        // which is what makes a validation failure surface at first read / host start rather
        // than at registration.
        string optionsName = NextOptionsName(sectionPath);

        services.AddSingleton(new FileSystemStorageRegistration(optionsName));

        var builder = services.AddOptions<FileSystemStorageOptions>(optionsName);

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
            sp.GetRequiredService<IOptionsMonitor<FileSystemStorageOptions>>().Get(optionsName),
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// Sequence for unique options-instance names within this assembly — one registration,
    /// one named instance (the name is an internal detail: tests read it back through the
    /// registration marker, never by hardcoding it).
    /// </summary>
    private static int s_optionsSequence;

    /// <summary>
    /// Names this registration's options instance: the section path when one was given
    /// (readable in diagnostics), the provider label otherwise, plus a sequence number that
    /// makes the name unique per registration.
    /// </summary>
    private static string NextOptionsName(string? sectionPath)
        => $"{sectionPath ?? "FileSystemStorage"}#{Interlocked.Increment(ref s_optionsSequence)}";
}

/// <summary>
/// Sentinel marker recording that the filesystem provider is already registered in this collection,
/// so a second <see cref="Setup.AddSaFileSystemFileStorage"/> call fails fast. Carries the
/// registration's options-instance name — the handle tests resolve the named instance by.
/// </summary>
internal sealed class FileSystemStorageRegistration(string optionsName)
{
    /// <summary>The name of this registration's named options instance.</summary>
    public string OptionsName { get; } =
        optionsName ?? throw new ArgumentNullException(nameof(optionsName));
}