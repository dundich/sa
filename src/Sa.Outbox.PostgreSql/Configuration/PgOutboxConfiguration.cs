using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Serialization;

namespace Sa.Outbox.PostgreSql.Configuration;

internal sealed class PgOutboxConfiguration(IServiceCollection services) : IPgOutboxConfiguration
{
    public IPgOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null)
    {
        if (configure != null)
        {
            services.AddSingleton(configure);
        }

        RegisterOutboxSettings();

        return this;
    }

    public IPgOutboxConfiguration WithDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null)
    {
        services.AddSaPostgreSqlDataSource(configure);
        return this;
    }

    public IPgOutboxConfiguration WithMessageSerializer(Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory)
    {
        services.RemoveAll<IOutboxMessageSerializer>();
        services.TryAddSingleton<IOutboxMessageSerializer>(messageSerializerFactory);
        return this;
    }

    public IPgOutboxConfiguration WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer
    {
        services.RemoveAll<IOutboxMessageSerializer>();
        services.TryAddSingleton<IOutboxMessageSerializer>(instance);
        return this;
    }

    internal IPgOutboxConfiguration WithDefaultSerializer()
    {
        services.TryAddSingleton<IOutboxMessageSerializer>(OutboxMessageSerializer.Instance);
        return this;
    }

    private void RegisterOutboxSettings()
    {
        services.TryAddSingleton<PgOutboxSettings>(sp =>
        {
            PgOutboxSettings settings = new();

            var configureActions = sp.GetServices<Action<IServiceProvider, PgOutboxSettings>>();

            foreach (var configureAction in configureActions)
                configureAction.Invoke(sp, settings);

            return settings;
        });

        RegisterComponentSettings();
    }

    private void RegisterComponentSettings()
    {
        services.TryAddSingleton<PgOutboxTableSettings>(sp => sp.GetRequiredService<PgOutboxSettings>().TableSettings);
        services.TryAddSingleton<PgOutboxMigrationSettings>(sp => sp.GetRequiredService<PgOutboxSettings>().MigrationSettings);
        services.TryAddSingleton<PgOutboxCleanupSettings>(sp => sp.GetRequiredService<PgOutboxSettings>().CleanupSettings);
        services.TryAddSingleton<PgOutboxConsumeSettings>(sp => sp.GetRequiredService<PgOutboxSettings>().ConsumeSettings);
    }
}
