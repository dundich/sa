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
    /// Configures the partition cache settings through the standard options pipeline.
    /// When <paramref name="configure"/> is <c>null</c>, the built-in defaults are used.
    /// </summary>
    /// <param name="configure">
    /// Optional callback receiving the <see cref="IPartSettingsBuilder{PartCacheSettings}"/> for this
    /// module: the section via <c>FromConfiguration</c>, the code settings via <c>Configure</c> and
    /// the pipeline escape hatch via <c>Options</c>.
    /// </param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddPartCache(Action<IPartSettingsBuilder<PartCacheSettings>>? configure = null);

    /// <summary>
    /// Configures the automated partition-migration schedule (pre-creates future partitions).
    /// </summary>
    /// <param name="configure">
    /// Optional callback receiving the <see cref="IPartSettingsBuilder{MigrationScheduleSettings}"/>
    /// for this module: the section via <c>FromConfiguration</c>, the code settings via
    /// <c>Configure</c> and the pipeline escape hatch via <c>Options</c>.
    /// </param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddPartMigrationSchedule(Action<IPartSettingsBuilder<MigrationScheduleSettings>>? configure = null);

    /// <summary>
    /// Configures the automated partition-cleanup schedule (drops old partitions past the retention window).
    /// </summary>
    /// <param name="configure">
    /// Optional callback receiving the <see cref="IPartSettingsBuilder{PartCleanupScheduleSettings}"/>
    /// for this module: the section via <c>FromConfiguration</c>, the code settings via
    /// <c>Configure</c> and the pipeline escape hatch via <c>Options</c>.
    /// </param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddPartCleanupSchedule(Action<IPartSettingsBuilder<PartCleanupScheduleSettings>>? configure = null);

    /// <summary>
    /// Configures the PostgreSQL data source through the standard options pipeline
    /// (<see cref="PgDataSourceOptions"/>).
    /// </summary>
    /// <param name="configure">
    /// Optional callback receiving the <see cref="IDataSourceBuilder"/> for this data source:
    /// the section via <c>FromConfiguration</c> and the options pipeline via <c>Options</c>.
    /// The only option is the connection string; pooling, timeout and search path live inside
    /// the string itself.
    /// </param>
    /// <returns>The same <see cref="IPartConfiguration"/> for chaining.</returns>
    IPartConfiguration AddDataSource(Action<IDataSourceBuilder>? configure = null);
}
