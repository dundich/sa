using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Provides extension methods for registering the filesystem file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers a filesystem file storage provider under an explicit name, backed by a temp-folder
    /// instance of the same name.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="name">
    /// The registration name — the key the filesystem storage and its <see cref="ITempFolder"/> are
    /// registered under (<c>sp.GetRequiredKeyedService&lt;ITempFolder&gt;(name)</c>). Must be unique
    /// per registration; a second call with the same name throws.
    /// </param>
    /// <param name="configure">
    /// The configuration channel: the section via <see cref="IFileSystemStorageBuilder.FromConfiguration"/>,
    /// the standard pipeline (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) via
    /// <see cref="IFileSystemStorageBuilder.Options"/>, and the mandatory root/I-O channel via
    /// <see cref="IFileSystemStorageBuilder.TempFolder"/>. Invoked once, immediately; its
    /// <c>Options(...)</c> actions are replayed after this method's own registrations, so their
    /// <c>Configure</c> runs last and their <c>Validate</c> adds to — rather than replaces — the
    /// built-in checks.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a filesystem storage with the same name is already registered, or when the
    /// mandatory <see cref="IFileSystemStorageBuilder.TempFolder"/> channel was not configured.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Registration is additive: every call registers one <see cref="IFileStorage"/> and one keyed
    /// <see cref="ITempFolder"/>, so several filesystem storages — each with its own root, basket and
    /// daily TTL — can coexist in one collection.
    /// </para>
    /// <para>
    /// The storage options pipeline runs in a fixed order: the section binding
    /// (<c>FromConfiguration</c>) and then the caller's <c>Configure</c> calls → <c>PostConfigure</c>
    /// (this method's normalisation) and then the caller's <c>PostConfigure</c> calls → validation.
    /// Everything the storage does happens through its <see cref="ITempFolder"/>: the root, the
    /// <c>fs://</c> path guarding, the cleanup activity and the transient-I-O retries are all the
    /// temp folder's. The storage defaults its temp folder's <see cref="TempFolderOptions.MaxAge"/>
    /// to <see cref="FileSystemStorageRegistrar.DefaultStorageMaxAge"/> (30 days) unless the section or the callback sets one.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        string name,
        Action<IFileSystemStorageBuilder>? configure = null)
        => FileSystemStorageRegistrar.Register(services, name, configure);
}
