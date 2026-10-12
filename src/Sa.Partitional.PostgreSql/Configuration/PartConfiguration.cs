using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql.Cache;
using Sa.Partitional.PostgreSql.Cleaning;
using Sa.Partitional.PostgreSql.Configuration.Builder;
using Sa.Partitional.PostgreSql.Migration;
using Sa.Partitional.PostgreSql.Repositories;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSql.Configuration;

internal sealed class PartConfiguration(IServiceCollection services, string? key = null) : IPartConfiguration
{

    public IPartConfiguration AddPartTables(Action<IServiceProvider, ISettingsBuilder> configure)
    {
        services
            .AddSettings(key, configure)
            .AddSqlBuilder(key)
            ;

        return this;
    }

    public IPartConfiguration AddDataSource(Action<IDataSourceBuilder>? configure = null)
    {
        if (key is null)
        {
            services.AddSaPostgreSqlDataSource(configure);
        }
        else if (configure is not null)
        {
            // A configuring call: this module pins its own named data source under the module key.
            // Without it the module falls back to the shared default data source (PgDataSourceResolver),
            // so several keyed modules can partition tables inside the same database. The keyed
            // chain of AddSaPartitional does not call this method by itself.
            services.AddSaPostgreSqlDataSource(key, configure);
        }

        // inner (idempotent: the keyed AddSaPartitional chain already registered it)
        services.AddPartRepository(key);
        return this;
    }

    public IPartConfiguration AddPartCache(Action<IPartSettingsBuilder<PartCacheSettings>>? configure = null)
    {
        services.AddPartCache(key, configure);
        return this;
    }

    public IPartConfiguration AddPartMigrationSchedule(Action<IPartSettingsBuilder<MigrationScheduleSettings>>? configure = null)
    {
        services.AddMigration(key, configure);
        return this;
    }

    public IPartConfiguration AddPartCleanupSchedule(Action<IPartSettingsBuilder<PartCleanupScheduleSettings>>? configure = null)
    {
        services.AddPartCleaning(key, configure);
        return this;
    }
}
