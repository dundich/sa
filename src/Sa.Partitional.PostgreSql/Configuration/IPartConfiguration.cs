using Sa.Data.PostgreSql;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Fluent configuration surface returned by <see cref="Setup.AddSaPartitional"/>.
/// Use this interface to optionally wire up partitioned tables, caching, migration scheduling, cleanup scheduling, and the data source.
/// </summary>
public interface IPartConfiguration
{
    /// <summary>
    /// Registers the partitioned-table schema definition.
    /// </summary>
    /// <param name="configure">An action that receives an <see cref="ISettingsBuilder"/> for declaring schemas and tables.</param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddPartTables(Action<IServiceProvider, ISettingsBuilder> configure);

    /// <summary>
    /// Enables the partition cache with optional custom settings.
    /// When <paramref name="configure"/> is <c>null</c>, the default cache window (1 day ahead) is used.
    /// </summary>
    /// <param name="configure">Optional action to tweak <see cref="PartCacheSettings"/>.</param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddPartCache(Action<IServiceProvider, PartCacheSettings>? configure = null);

    /// <summary>
    /// Configures the automated partition-migration schedule (pre-creates future partitions).
    /// </summary>
    /// <param name="configure">Optional action to tweak <see cref="MigrationScheduleSettings"/>.</param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddPartMigrationSchedule(Action<IServiceProvider, MigrationScheduleSettings>? configure = null);

    /// <summary>
    /// Configures the automated partition-cleanup schedule (drops old partitions past the retention window).
    /// </summary>
    /// <param name="configure">Optional action to tweak <see cref="PartCleanupScheduleSettings"/>.</param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddPartCleanupSchedule(Action<IServiceProvider, PartCleanupScheduleSettings>? configure = null);

    /// <summary>
    /// Configures the PostgreSQL data source via <see cref="IPgDataSourceSettingsBuilder"/>.
    /// </summary>
    /// <param name="configure">Optional action to set connection strings, pooling, retries, etc.</param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null);
}
