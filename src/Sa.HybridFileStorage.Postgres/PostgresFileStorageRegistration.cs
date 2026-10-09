using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Sentinel marker holding the registration state of the PostgreSQL file storage provider.
/// Ensures the partitioning setup, schedules, and <see cref="IFileStorage"/> are registered exactly once
/// and lets a conflicting second registration fail fast. Carries the registration's
/// options-instance name — the handle tests resolve the named instance by.
/// </summary>
internal sealed class PostgresFileStorageRegistration(
    string optionsName,
    IPartConfiguration configuration)
{
    /// <summary>The name of this registration's named options instance.</summary>
    public string OptionsName { get; } = optionsName ?? throw new ArgumentNullException(nameof(optionsName));

    /// <summary>
    /// Gets the partition configuration produced by the registration.
    /// </summary>
    public IPartConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));
}
