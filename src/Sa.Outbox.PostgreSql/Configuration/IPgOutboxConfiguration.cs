using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PostgreSql.Configuration;

public interface IPgOutboxConfiguration
{
    IPgOutboxConfiguration WithMessageSerializer(
        Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory);
    IPgOutboxConfiguration WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer;
    IPgOutboxConfiguration WithMessageSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IOutboxMessageSerializer;
    IPgOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null);
    IPgOutboxConfiguration WithDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null);
}
