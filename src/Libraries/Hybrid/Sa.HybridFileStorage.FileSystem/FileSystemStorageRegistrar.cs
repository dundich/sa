using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Data.TempFolder;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// The one place that knows how the filesystem provider is registered: <see cref="Setup.AddSaFileSystemFileStorage"/>
/// delegates here — the name-uniqueness guard, the options pipeline, the temp-folder channel and
/// the <see cref="IFileStorage"/> descriptor.
/// </summary>
internal static class FileSystemStorageRegistrar
{
    /// <summary>
    /// The <see cref="TempFolderOptions.MaxAge"/> a filesystem storage falls back to when neither the
    /// temp-folder section nor the callback sets one. Storage files are meant to outlive a normal
    /// scratch folder, so the 24-hour temp-folder default is raised to 30 days.
    /// </summary>
    internal static readonly TimeSpan DefaultStorageMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Marker value seeded into <see cref="TempFolderOptions.MaxAge"/> on the pre-section defaults
    /// channel; <see cref="Setup.AddSaFileSystemFileStorage"/> replaces an untouched sentinel with
    /// <see cref="DefaultStorageMaxAge"/> in a final post-configure. The section and the caller's
    /// actions both run after the defaults channel and overwrite it, so a surviving sentinel means
    /// "the default was never overridden". Distinct from any legal value, so "untouched" is
    /// detectable without a second options channel.
    /// </summary>
    internal static readonly TimeSpan SentinelMaxAge = TimeSpan.FromTicks(-1);

    /// <summary>
    /// Performs the shared registration logic of <see cref="Setup.AddSaFileSystemFileStorage"/>.
    /// </summary>
    public static IServiceCollection Register(
        IServiceCollection services,
        string name,
        Action<IFileSystemStorageBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A registration name is required.", nameof(name));
        }

        // One storage per name: two registrations under the same key would fight over the keyed
        // temp folder and the named options instance. Different names are independent. The keyed
        // IFileStorage is the registration's real handle, so the guard is anchored on it — and it
        // is checked here, before AddSaTempFolder would raise its own (less specific) duplicate.
        if (services.Any(d => d.ServiceType == typeof(IFileStorage) && Equals(d.ServiceKey, name)))
        {
            throw new InvalidOperationException(
                $"A filesystem storage named '{name}' is already registered in this service " +
                "collection. Registration names must be unique — pick another name for the second " +
                "instance.");
        }

        var storageBuilder = new FileSystemStorageBuilder();
        configure?.Invoke(storageBuilder);

        // The root and all file I/O belong to the temp folder; a storage without the channel has
        // nowhere to put a file, so fail here where the omission is visible.
        if (storageBuilder.TempFolderActions.Count == 0)
        {
            throw new InvalidOperationException(
                "A filesystem storage needs a root: configure the mandatory TempFolder channel, " +
                "e.g. AddSaFileSystemFileStorage(\"share\", b => b.TempFolder(tb => tb " +
                "FromConfiguration(\"TempFolder\"))).");
        }

        // Fixed slot: the section binds after the callback has recorded it, before its
        // Options(...) actions replay — wherever those calls sit in the callback.
        var sectionPath = storageBuilder.ConfigSectionPath;

        // One registration, one named options instance, named after the registration itself: the
        // name is unique per collection (the guard above) and readable in diagnostics, which is
        // all a named instance needs. Validation is deferred by IOptionsMonitor, so an invalid
        // configuration surfaces at first resolve / host start rather than at registration.
        var builder = services.AddOptions<FileSystemStorageOptions>(name);

        if (sectionPath is not null)
        {
            builder.BindConfiguration(sectionPath);
        }

        // Post-configuration: trim the names so validation and the file-ID scheme see clean values.
        // Runs before validation, so the checks below apply to the normalised result.
        builder.PostConfigure(static options =>
        {
            options.StorageType = options.StorageType.Trim();
            options.Basket = options.Basket.Trim();
        });

        builder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FileSystemStorageOptions>, FileSystemStorageOptionsValidator>());

        // Replayed in this slot — after the section binding and after this method's own
        // PostConfigure — so the caller's Configure beats the section and its Validate runs
        // after ours.
        foreach (var settingsAction in storageBuilder.SettingsActions)
        {
            settingsAction(builder);
        }

        services.TryAddSingleton(TimeProvider.System);

        // The temp folder is registered under the very same key; the sentinel dance gives a
        // storage a 30-day default TTL without stealing an explicitly configured one. The temp
        // folder's pipeline order is: defaults (the sentinel) → section bind → the caller's
        // Configure → the caller's PostConfigure (delegates in this assembly run here) → validation.
        services.AddSaTempFolder(name, tempFolder =>
        {
            // Pre-section default (lowest precedence). The section bind and the caller's Configure
            // both run after this and overwrite it; a surviving sentinel is therefore proof that
            // neither the section nor the caller set MaxAge.
            tempFolder.Defaults(static o => o.MaxAge = SentinelMaxAge);

            foreach (var tempFolderAction in storageBuilder.TempFolderActions)
            {
                tempFolderAction(tempFolder);
            }

            // Recorded last: any surviving sentinel means neither the section nor the caller set
            // MaxAge, so the storage default applies. Validation runs after every PostConfigure.
            tempFolder.Options(static ob => ob.PostConfigure(static o =>
            {
                if (o.MaxAge == SentinelMaxAge)
                {
                    o.MaxAge = DefaultStorageMaxAge;
                }
            }));

            // A storage root must be explicit and must not be the system temp directory.
            tempFolder.Options(static ob => ob.Validate(
                static o => IsUsableStorageRoot(o.RootPath),
                "The filesystem storage root must be an explicit directory outside the system temp " +
                "folder — set RootPath under the TempFolder channel or its bound configuration " +
                "section (the OS wipes the system temp folder and shares it with every other " +
                "temp-folder instance)."));
        });

        // One storage per name, and the name keys everything: the keyed temp folder, the keyed
        // IFileStorage and its named options instance. Both views — keyed and the historical
        // unkeyed one — resolve the same instance, so one host can run several storages and
        // `GetRequiredService<IFileStorage>()` keeps working for the single-storage case.
        services.AddKeyedSingleton<IFileStorage>(name, (sp, _) => new FileSystemStorage(
            sp.GetRequiredKeyedService<ITempFolder>(name),
            sp.GetRequiredService<IOptionsMonitor<FileSystemStorageOptions>>().Get(name),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IFileStorage>(
            sp => sp.GetRequiredKeyedService<IFileStorage>(name));

        return services;
    }

    /// <summary>
    /// Determines whether <paramref name="root"/> is usable as a filesystem-storage root: it must
    /// be set and must not be the system temp directory. A malformed path is left to
    /// <see cref="TempFolderOptions.Validate"/>, which owns that message.
    /// </summary>
    private static bool IsUsableStorageRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string full;
        string systemTemp;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim()));
            systemTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return !string.Equals(full, systemTemp, comparison);
    }
}
