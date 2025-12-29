using Sa.Data.PostgreSql;

namespace Sa.Partitional.PostgreSql;

public interface IPartConfiguration
{
    IPartConfiguration AddPartTables(Action<IServiceProvider, ISettingsBuilder> configure);
    IPartConfiguration AddPartCache(Action<IServiceProvider, PartCacheSettings>? configure = null);
    IPartConfiguration AddPartMigrationSchedule(Action<IServiceProvider, MigrationScheduleSettings>? configure = null);
    IPartConfiguration AddPartCleanupSchedule(Action<IServiceProvider, PartCleanupScheduleSettings>? configure = null);

    IPartConfiguration AddDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null);
}
