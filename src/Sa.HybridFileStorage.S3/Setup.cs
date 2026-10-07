using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.S3;

/// <summary>
/// Provides extension methods for registering the S3 file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the S3 file storage provider using the standard options pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">
    /// The configuration channel: the section via <see cref="IS3FileStorageBuilder.FromConfiguration"/>
    /// and the standard pipeline (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) via
    /// <see cref="IS3FileStorageBuilder.Options"/>, in the same delegate. Invoked once, immediately;
    /// its <c>Options(...)</c> actions are replayed after this method's own registrations, so their
    /// <c>Configure</c> runs last and their <c>Validate</c> adds to — rather than replaces — the
    /// built-in checks.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Registration is additive: every call registers one <see cref="IFileStorage"/> with its own
    /// named options instance and its own bucket client, so several S3 storages — even several in
    /// one basket, which is how two buckets back one folder — can coexist in one collection.
    /// <para>
    /// The options pipeline runs in a fixed order: the section binding (<c>FromConfiguration</c>,
    /// raw values) and then the caller's <c>Options(...)</c> <c>Configure</c> calls →
    /// <c>PostConfigure</c> (this method's normalisation) and then the caller's
    /// <c>Options(...)</c> <c>PostConfigure</c> calls → validation. Validation therefore sees the
    /// normalised <see cref="S3BucketClientSetupOptions.Endpoint"/> and trimmed names, and
    /// <c>ValidateOnStart()</c> turns an invalid configuration into an
    /// <see cref="OptionsValidationException"/> at host start instead of a bare
    /// <see cref="UriFormatException"/> from inside the bucket client on the first upload.
    /// <para>
    /// The bucket client is built from these very options (see <see cref="S3BucketClientSetupOptions"/>),
    /// so there is a single set of values from the configuration section all the way down to the HTTP
    /// pipeline. The client is keyed by this registration's name — its own endpoint, bucket,
    /// credentials, timeouts and pool lifetime — so two storages never share a client or an options
    /// instance, however similar their baskets and endpoints are.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaS3FileStorage(
        this IServiceCollection services,
        Action<IS3FileStorageBuilder>? configure = null)
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

/// <summary>
/// Per-registration handle: records the registration's options-instance name — the key its named
/// options and its keyed bucket client share. Tests resolve the named instance and the client by
/// reading this name back, never by hardcoding it.
/// </summary>
internal sealed class S3FileStorageRegistration(string optionsName)
{
    /// <summary>The name of this registration's named options instance.</summary>
    public string OptionsName { get; } =
        optionsName ?? throw new ArgumentNullException(nameof(optionsName));
}
