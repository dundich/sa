using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql.Configuration;

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
}
