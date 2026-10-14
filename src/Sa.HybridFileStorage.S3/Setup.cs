using Microsoft.Extensions.DependencyInjection;
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
        => S3FileStorageRegistrar.Register(services, configure);
}
