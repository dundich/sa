using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql.Cache;
using Sa.Partitional.PostgreSql.Cleaning;
using Sa.Partitional.PostgreSql.Configuration.Builder;
using Sa.Partitional.PostgreSql.Migration;
using Sa.Partitional.PostgreSql.Repositories;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSql.Configuration;

internal sealed class PartConfiguration(IServiceCollection services) : IPartConfiguration
{

    public IPartConfiguration AddPartTables(Action<IServiceProvider, ISettingsBuilder> configure)
    {
        services
            .AddSettigs(configure)
            .AddSqlBuilder()
            ;

        return this;
    }

    public IPartConfiguration AddDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null)
    {
        services.AddPgDataSource(configure);

        // inner
        services.AddPartRepository();
        return this;
    }

    public IPartConfiguration AddPartCache(Action<IServiceProvider, PartCacheSettings>? configure = null)
    {
        services.AddPartCache(configure);
        return this;
    }

    public IPartConfiguration AddPartMigrationSchedule(Action<IServiceProvider, PartMigrationScheduleSettings>? configure = null)
    {
        services.AddPartMigration(configure);
        return this;
    }

    public IPartConfiguration AddPartCleanupSchedule(Action<IServiceProvider, PartCleanupScheduleSettings>? configure = null)
    {
        services.AddPartCleaning(configure);
        return this;
    }
}
