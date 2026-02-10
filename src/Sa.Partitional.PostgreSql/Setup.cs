using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql.Configuration;

namespace Sa.Partitional.PostgreSql;

public static class Setup
{
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
