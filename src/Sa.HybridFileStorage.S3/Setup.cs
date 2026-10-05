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
    /// An optional callback receiving the <see cref="OptionsBuilder{TOptions}"/> for this provider, so
    /// configuration goes through the standard <c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>
    /// methods rather than a bespoke overload.
    /// </param>
    /// <param name="configSectionPath">
    /// An optional configuration section to bind the options from, e.g. <c>"S3FileStorage"</c>.
    /// Bound first, so a <c>Configure</c> call in <paramref name="configure"/> has the last word.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when an S3 storage was already registered in this collection.</exception>
    /// <remarks>
    /// The options pipeline runs in a fixed order: <c>Configure</c> (pre-initialisation, raw values) →
    /// <c>PostConfigure</c> (this method's normalisation) → <c>PostConfigure</c> calls made in
    /// <paramref name="configure"/> → validation. Validation therefore sees the normalised
    /// <see cref="S3BucketClientSetupOptions.Endpoint"/> and trimmed names, and
    /// <c>ValidateOnStart()</c> turns an invalid configuration into an
    /// <see cref="OptionsValidationException"/> at host start instead of a bare
    /// <see cref="UriFormatException"/> from inside the bucket client on the first upload.
    /// <para>
    /// The callback is invoked after this method's own registrations, so its <c>Configure</c> runs last
    /// and its <c>Validate</c> adds to — rather than replaces — the built-in checks.
    /// </para>
    /// <para>
    /// The bucket client is built from these very options (see <see cref="S3BucketClientSetupOptions"/>),
    /// so there is a single set of values from the configuration section all the way down to the HTTP
    /// pipeline.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaS3FileStorage(
        this IServiceCollection services,
        Action<OptionsBuilder<S3FileStorageOptions>>? configure = null,
        string? configSectionPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The provider owns the unnamed S3FileStorageOptions instance. A second call would add a second
        // IFileStorage built from those same options, and its Configure callback would stack on top of
        // the first one's — one storage with a mangled basket plus a copy of it. Fail here, where the
        // cause is visible, mirroring AddSaFileSystemFileStorage.
        if (services.Any(d => d.ServiceType == typeof(S3FileStorageRegistration)))
        {
            throw new InvalidOperationException(
                "AddSaS3FileStorage has already been registered in this service collection. " +
                "The second call would register another IFileStorage over the same options instance, " +
                "and both Configure callbacks would apply, so the storage would silently use merged " +
                "settings. Register only one S3 storage per service collection.");
        }

        services.AddSingleton(new S3FileStorageRegistration());

        var builder = services.AddOptions<S3FileStorageOptions>();

        if (configSectionPath is not null)
        {
            builder.BindConfiguration(configSectionPath);
        }

        // Runs before validation, so the checks in Validate() apply to the normalised result.
        builder.PostConfigure(static options => options.Normalize());

        builder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<S3FileStorageOptions>, S3FileStorageOptionsValidator>());

        // Invoked last, so the caller's Configure runs after any section binding and its PostConfigure
        // and Validate run after this method's own.
        configure?.Invoke(builder);

        // The bucket client is configured from the same instance — same endpoint, credentials,
        // timeouts and pool lifetime — so there is no second place to keep in sync.
        services.AddSaS3BucketClientCore(sp => sp.GetRequiredService<IOptions<S3FileStorageOptions>>().Value);

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IFileStorage>(sp => new S3FileStorage(
            sp.GetRequiredService<IS3BucketClient>(),
            sp.GetRequiredService<IOptions<S3FileStorageOptions>>().Value,
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}

/// <summary>
/// Sentinel marker recording that the S3 provider is already registered in this collection, so a
/// second <see cref="Setup.AddSaS3FileStorage"/> call fails fast instead of silently stacking a
/// second <see cref="IFileStorage"/> over the same options instance.
/// </summary>
internal sealed class S3FileStorageRegistration
{
}
