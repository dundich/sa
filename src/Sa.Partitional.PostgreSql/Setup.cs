using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql.Configuration;

namespace Sa.Partitional.PostgreSql;

public static class Setup
{
    public static IPartConfiguration AddPartitional(this IServiceCollection services, Action<IServiceProvider, ISettingsBuilder> configure, bool? asJob = null)
    {
        services.AddSaInfrastructure();
        services.TryAddSingleton<IPartitionManager, PartitionManager>();

        return new PartConfiguration(services)
            // defaults
            .AddDataSource()
            .AddPartTables(configure)
            .AddPartCache()
            .AddPartMigrationSchedule((_, settings) => settings.AsJob = asJob ?? settings.AsJob)
            .AddPartCleanupSchedule((_, settings) => settings.AsJob = asJob ?? settings.AsJob)
            ;
    }
}
