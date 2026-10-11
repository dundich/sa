using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql.Cache;
using Sa.Partitional.PostgreSql.Configuration;
using Sa.Partitional.PostgreSql.Repositories;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Provides extension methods for registering the Sa.Partitional.PostgreSql services into the ASP.NET Core dependency injection container.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers all Sa.Partitional.PostgreSql services (partition manager, cache, migration schedule, cleanup schedule, and data source) into the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">An action to configure partitioned tables via <see cref="ISettingsBuilder"/>.</param>
    /// <param name="AsBackgroundJob">
    /// Optionally forces both migration and cleanup to run as background jobs (<c>true</c>) or disables them (<c>false</c>).
    /// When <c>null</c>, each component uses its own default.
    /// </param>
    /// <returns>An <see cref="IPartConfiguration"/> for chained configuration of optional components.</returns>
    public static IPartConfiguration AddSaPartitional(this IServiceCollection services,
        Action<IServiceProvider, ISettingsBuilder> configure,
        bool? AsBackgroundJob = null)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IPartitionManager, PartitionManager>();

        return new PartConfiguration(services)
            // defaults
            .AddDataSource()
            .AddPartTables(configure)
            .AddPartCache()
            .AddPartMigrationSchedule((_, settings) => settings.AsBackgroundJob = AsBackgroundJob ?? settings.AsBackgroundJob)
            .AddPartCleanupSchedule((_, settings) => settings.AsBackgroundJob = AsBackgroundJob ?? settings.AsBackgroundJob)
            ;
    }

    /// <summary>
    /// Registers an independent, <b>keyed</b> partitional module under <paramref name="key"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="key">
    /// The unique module key. Every service of the module is registered keyed under it —
    /// <see cref="IPartitionManager"/>, <see cref="IPartRepository"/>, <see cref="IMigrationService"/>,
    /// <see cref="IPartCleanupService"/>, the <see cref="ISettingsBuilder"/> / <see cref="ISqlBuilder"/>
    /// and the settings (<see cref="PartCacheSettings"/>, <see cref="MigrationScheduleSettings"/>,
    /// <see cref="PartCleanupScheduleSettings"/>) — plus its own named schedule with the migration and
    /// cleanup background jobs. Resolve them with
    /// <c>sp.GetRequiredKeyedService&lt;IPartitionManager&gt;(key)</c>. Must be unique per registration,
    /// non-empty, and not <see cref="Sa.Schedule.Setup.DefaultName"/>.
    /// </param>
    /// <param name="configure">An action to configure partitioned tables via <see cref="ISettingsBuilder"/> — scoped to this module only; repeated calls with the same key merge.</param>
    /// <param name="AsBackgroundJob">
    /// Optionally forces both migration and cleanup to run as background jobs (<c>true</c>) or disables them (<c>false</c>).
    /// When <c>null</c>, each component uses its own default.
    /// </param>
    /// <returns>An <see cref="IPartConfiguration"/> for chained configuration of optional components (also scoped to this module).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="configure"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty, whitespace, or the reserved <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    /// <remarks>
    /// Keyed modules fully coexist: each one has its own partition-table declarations, its own cache,
    /// its own migration and cleanup settings and its own named schedule (a dedicated scheduler with
    /// its own hosted service when background jobs are enabled). Different keys never see each other's
    /// services. The data source is the module's <b>named</b> data source when the chain configures one
    /// (the <c>AddDataSource(...)</c> call), and the shared <b>default</b> data source otherwise —
    /// so several modules can partition tables inside the same database.
    /// </remarks>
    public static IPartConfiguration AddSaPartitional(this IServiceCollection services,
        string key,
        Action<IServiceProvider, ISettingsBuilder> configure,
        bool? AsBackgroundJob = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key == Sa.Schedule.Setup.DefaultName)
        {
            throw new ArgumentException(
                $"The key \"{Sa.Schedule.Setup.DefaultName}\" is reserved for the default (unkeyed) " +
                "partitional module. Use the keyless AddSaPartitional overload or pick a different key.",
                nameof(key));
        }

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddKeyedSingleton<IPartitionManager>(key, (sp, _) => new PartitionManager(
            sp.GetRequiredKeyedService<IPartCache>(key),
            sp.GetRequiredKeyedService<IMigrationService>(key)));

        // The module owns no data source of its own here. Its repository resolves a named data
        // source registered under the module key when one exists, and the shared default data
        // source otherwise (see PgDataSourceResolver). Pin a named one with the chained
        // AddDataSource(...) call.
        services.AddPartRepository(key);

        return new PartConfiguration(services, key)
            // defaults
            .AddPartTables(configure)
            .AddPartCache()
            .AddPartMigrationSchedule((_, settings) => settings.AsBackgroundJob = AsBackgroundJob ?? settings.AsBackgroundJob)
            .AddPartCleanupSchedule((_, settings) => settings.AsBackgroundJob = AsBackgroundJob ?? settings.AsBackgroundJob)
            ;
    }
}
