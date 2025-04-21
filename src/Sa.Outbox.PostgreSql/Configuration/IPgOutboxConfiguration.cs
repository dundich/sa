using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Serialization;

namespace Sa.Outbox.PostgreSql;

public interface IPgOutboxConfiguration
{
    IPgOutboxConfiguration WithMessageSerializer(Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory);
    IPgOutboxConfiguration ConfigureOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null);
    IPgOutboxConfiguration ConfigureDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null);
}
