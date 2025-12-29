using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Serialization;

namespace Sa.Outbox.PostgreSql.Configuration;

internal sealed class PgOutboxConfiguration(IServiceCollection services) : IPgOutboxConfiguration
{
    private static readonly ConcurrentDictionary<
        IServiceCollection, HashSet<Action<IServiceProvider, PgOutboxSettings>>> s_invokers = [];

    public IPgOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null)
    {
        if (configure != null)
        {
            if (s_invokers.TryGetValue(services, out var invokers))
            {
                invokers.Add(configure);
            }
            else
            {
                s_invokers[services] = [configure];
            }
        }


        services.TryAddSingleton<PgOutboxSettings>(sp =>
        {
            PgOutboxSettings settings = new();

            if (s_invokers.TryGetValue(services, out var invokers))
            {
                foreach (Action<IServiceProvider, PgOutboxSettings> build in invokers)
                    build.Invoke(sp, settings);

                s_invokers.Remove(services, out _);
            }

            return settings;
        });

        AddSettings();
        return this;
    }

    public IPgOutboxConfiguration WithDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null)
    {
        services.AddPgDataSource(configure);
        return this;
    }

    public IPgOutboxConfiguration WithMessageSerializer(Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory)
    {
        services.TryAddSingleton<IOutboxMessageSerializer>(messageSerializerFactory);
        return this;
    }

    public IPgOutboxConfiguration WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer
    {
        services.TryAddSingleton<IOutboxMessageSerializer>(instance);
        return this;
    }

    private void AddSettings()
    {
        services.TryAddSingleton<PgOutboxTableSettings>(sp => sp.GetRequiredService<PgOutboxSettings>().TableSettings);
        services.TryAddSingleton<PgOutboxMigrationSettings>(sp => sp.GetRequiredService<PgOutboxSettings>().MigrationSettings);
        services.TryAddSingleton<PgOutboxCleanupSettings>(sp => sp.GetRequiredService<PgOutboxSettings>().CleanupSettings);
        services.TryAddSingleton<PgOutboxConsumeSettings>(sp => sp.GetRequiredService<PgOutboxSettings>().ConsumeSettings);
    }
}
