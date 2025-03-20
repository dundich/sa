using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql;

public interface IPgOutboxConfiguration
{
    IPgOutboxConfiguration WithPgOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null);
    IPgOutboxConfiguration AddDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null);
}
