using Microsoft.Extensions.Configuration;

namespace Sa.Configuration;

/// <summary>
/// Priority layer a configuration source belongs to.
/// </summary>
/// <remarks>
/// The layer only describes intent and ordering. Registration order is what actually decides
/// precedence in <see cref="IConfiguration"/>; the builder places <see cref="Static"/> sources
/// before <see cref="Dynamic"/> ones, and the post-processing pass after both.
/// </remarks>
public enum SaConfigurationLayer
{
    /// <summary>Files, environment variables, command-line arguments, secrets — changed by editing a file or restarting.</summary>
    Static,

    /// <summary>A store that can change while the application runs (a database, a remote service).</summary>
    Dynamic
}

/// <summary>
/// Marks a configuration source whose values can change at runtime, so that
/// <see cref="ISaConfigurationReloader.ReloadDynamicAsync"/> reloads it without touching the static
/// sources.
/// </summary>
/// <remarks>
/// The implementation is responsible for registering the provider it creates into
/// <see cref="SaConfigurationRuntime"/>(the runtime is reachable from the
/// <see cref="IConfigurationBuilder"/> passed to <see cref="IConfigurationSource.Build"/>), and for
/// returning the same provider instance on every build of that source.
/// </remarks>
public interface ISaDynamicConfigurationSource : IConfigurationSource;

/// <summary>
/// A configuration provider that can load asynchronously — a database-backed provider, for
/// instance. The reloader uses this interface when it is available and falls back to the
/// synchronous <see cref="IConfigurationProvider.Load"/> otherwise.
/// </summary>
public interface ISaAsyncConfigurationProvider
{
    /// <summary>
    /// Reloads the provider's data and raises its reload token.
    /// </summary>
    ValueTask LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A provider that can refresh itself and notify its consumers.
/// </summary>
/// <remarks>
/// .NET 10 no longer exposes a <c>Reload</c> method on <see cref="IConfigurationProvider"/>, and
/// <see cref="ConfigurationRoot.Reload"/> raises the root token itself. A provider that is refreshed
/// outside the root — by <see cref="ISaConfigurationReloader"/> — has to raise its own token, which
/// is what this interface exposes.
/// </remarks>
public interface ISaReloadableConfigurationProvider
{
    /// <summary>Reloads the data and raises the provider's reload token.</summary>
    void Reload();
}

/// <summary>
/// Reloads the dynamic layers of an <see cref="IConfiguration"/> — and only them.
/// </summary>
/// <remarks>
/// <see cref="IConfigurationRoot.Reload"/> reloads every provider, including the files on disk.
/// This service reloads only the sources registered as <see cref="ISaDynamicConfigurationSource"/>,
/// then recomputes the post-processing pass (secret placeholders, key aliases over the fresh
/// values) and raises the reload tokens, so consumers see a consistent snapshot.
/// <para>
/// No background polling is registered by default: call <c>AddSaConfigurationRuntime</c> to get the
/// service and drive it yourself, or enable the periodic option explicitly.
/// </para>
/// </remarks>
public interface ISaConfigurationReloader
{
    /// <summary>Number of dynamic providers registered so far (diagnostics).</summary>
    int DynamicSourceCount { get; }

    /// <summary>
    /// Reloads the dynamic providers and then the post-processing pass.
    /// </summary>
    /// <remarks>Throws when a source fails; the previous snapshot stays in effect.</remarks>
    void ReloadDynamic();

    /// <summary>
    /// Asynchronously reloads the dynamic providers and then the post-processing pass.
    /// </summary>
    /// <remarks>
    /// Concurrent calls are serialized, so two callers never race inside the same provider. A
    /// failure propagates to the caller; the provider that failed keeps its previous snapshot.
    /// </remarks>
    ValueTask ReloadDynamicAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the registered late-bound initializers (a data source resolved from DI, for instance)
    /// and performs the first dynamic load.
    /// </summary>
    /// <remarks>
    /// Idempotent: repeated calls only refresh the dynamic layer. Call it once after the host is
    /// built when a source was registered with a connection resolved from the container.
    /// </remarks>
    ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default);
}
