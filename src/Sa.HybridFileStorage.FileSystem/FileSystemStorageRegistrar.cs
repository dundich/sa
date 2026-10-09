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
        // temp folder and the named options instance. Different names are independent.
        if (services.Any(d => d.ServiceType == typeof(FileSystemStorageRegistration) && Equals(d.ServiceKey, name)))
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

        // One registration, one named options instance. The name is an internal detail (the
        // section path or the registration key plus a sequence number) — tests read it back
        // through the keyed registration marker. IOptionsMonitor resolves it, so an invalid
        // configuration surfaces at first resolve / host start rather than at registration.
        string optionsName = NextOptionsName(sectionPath ?? name);

        services.AddKeyedSingleton(name, new FileSystemStorageRegistration(name, optionsName));

        var builder = services.AddOptions<FileSystemStorageOptions>(optionsName);

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

        services.AddSingleton<IFileStorage>(sp => new FileSystemStorage(
            sp.GetRequiredKeyedService<ITempFolder>(name),
            sp.GetRequiredService<IOptionsMonitor<FileSystemStorageOptions>>().Get(optionsName),
            sp.GetRequiredService<TimeProvider>()));

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

    /// <summary>
    /// Sequence for unique options-instance names within this assembly — one registration,
    /// one named instance (the name is an internal detail: tests read it back through the
    /// registration marker, never by hardcoding it).
    /// </summary>
    private static int s_optionsSequence;

    /// <summary>
    /// Names this registration's options instance: the section path when one was given
    /// (readable in diagnostics), the registration key otherwise, plus a sequence number that
    /// makes the name unique per registration.
    /// </summary>
    private static string NextOptionsName(string label)
        => $"{label}#{Interlocked.Increment(ref s_optionsSequence)}";
}
