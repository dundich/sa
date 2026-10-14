using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.S3;

/// <summary>
/// The one place that knows how the S3 provider is registered: <see cref="Setup.AddSaS3FileStorage"/>
/// delegates here — the guard-free registration core, from the named options instance down to the
/// keyed bucket client and the <see cref="IFileStorage"/> descriptor.
/// </summary>
internal static class S3FileStorageRegistrar
{
    /// <summary>
    /// Performs the shared registration logic of <see cref="Setup.AddSaS3FileStorage"/>.
    /// </summary>
    public static IServiceCollection Register(
        IServiceCollection services,
        Action<IS3FileStorageBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        S3FileStorageBuilder? storageBuilder = null;

        if (configure is not null)
        {
            storageBuilder = new S3FileStorageBuilder();
            configure(storageBuilder);
        }

        // Fixed slot: the section binds after the callback has recorded it, before its
        // Options(...) actions replay — wherever those calls sit in the callback.
        var sectionPath = storageBuilder?.ConfigSectionPath;

        // One registration, one unique name: this registration's named options instance and its
        // keyed bucket client share it, so a storage and its client always resolve together. The
        // name is an internal detail (the section path when one was given, the provider label plus
        // a sequence number otherwise) — tests reach it through the registration marker.
        string optionsName = NextOptionsName(sectionPath);

        services.AddSingleton(new S3FileStorageRegistration(optionsName));

        var builder = services.AddOptions<S3FileStorageOptions>(optionsName);

        if (sectionPath is not null)
        {
            builder.BindConfiguration(sectionPath);
        }

        // Runs before validation, so the checks in Validate() apply to the normalised result.
        builder.PostConfigure(static options => options.Normalize());

        builder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<S3FileStorageOptions>, S3FileStorageOptionsValidator>());

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

        // The bucket client is configured from this very options instance — its endpoint, bucket,
        // credentials, timeouts and pool lifetime — and is keyed by the registration name, so each
        // S3 storage gets its own client (even two storages with the same basket never share one).
        // IOptionsMonitor caches one instance per name, so both consumers below receive the same
        // reference.
        services.AddSaS3BucketClientCore(optionsName, sp =>
            sp.GetRequiredService<IOptionsMonitor<S3FileStorageOptions>>().Get(optionsName));

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IFileStorage>(sp => new S3FileStorage(
            sp.GetRequiredKeyedService<IS3BucketClient>(optionsName),
            sp.GetRequiredService<IOptionsMonitor<S3FileStorageOptions>>().Get(optionsName),
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
        => $"{sectionPath ?? "S3FileStorage"}#{Interlocked.Increment(ref s_optionsSequence)}";
}
