using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Sentinel marker holding the registration state of one keyed PostgreSQL file storage provider.
/// Registered keyed by the registration key, it makes a second registration with the same key fail
/// fast — a duplicate would collide on the named options instance, the keyed partitional module and
/// the keyed storage. Carries the derived partitional module key and the options-instance name,
/// which tests and diagnostics resolve the registration by.
/// </summary>
internal sealed class PostgresFileStorageRegistration(
    string key,
    string moduleKey,
    string optionsName,
    IPartConfiguration configuration)
{
    /// <summary>The registration key the storage and its keyed services are registered under.</summary>
    public string Key { get; } = key ?? throw new ArgumentNullException(nameof(key));

    /// <summary>
    /// The key of the registration's partitional module. Derived from <see cref="Key"/> and namespaced,
    /// so a file storage never collides with a user-registered partitional module using the same key.
    /// </summary>
    public string ModuleKey { get; } = moduleKey ?? throw new ArgumentNullException(nameof(moduleKey));

    /// <summary>The name of this registration's named options instance (the registration key).</summary>
    public string OptionsName { get; } = optionsName ?? throw new ArgumentNullException(nameof(optionsName));

    /// <summary>
    /// Gets the partition configuration produced by the registration (its keyed partitional module).
    /// </summary>
    public IPartConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));
}
