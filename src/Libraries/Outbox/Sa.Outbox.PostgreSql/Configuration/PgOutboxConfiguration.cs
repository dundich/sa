using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Serialization;
using System.Diagnostics.CodeAnalysis;

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

    public IPgOutboxConfiguration WithDataSource(Action<IDataSourceBuilder>? configure = null)
    {
        services.AddSaPostgreSqlDataSource(configure);
        return this;
    }

    public IPgOutboxConfiguration WithMessageSerializer(
        Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory)
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

    public IPgOutboxConfiguration WithMessageSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IOutboxMessageSerializer
    {
        services.RemoveAll<IOutboxMessageSerializer>();
        services.TryAddSingleton<IOutboxMessageSerializer, TService>();
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

            var connectionSchema = sp.GetService<IPgDataSource>()?.GetSearchPath();

            if (!string.IsNullOrWhiteSpace(connectionSchema))
            {
                settings.TableSettings.WithSchema(connectionSchema);
            }

            var configureActions = sp.GetServices<Action<IServiceProvider, PgOutboxSettings>>();

            foreach (var configureAction in configureActions)
                configureAction.Invoke(sp, settings);

            // Validated here — after every configure action, before the SQL templates are built — so a
            // name that would break the generated DDL fails at startup instead of on the first batch.
            //
            // The consumer groups are read from their manager when the delivery side is registered:
            // a group id is half of a task- and delivery-partition name, and a group that cannot fit
            // PostgreSQL's identifier limit would otherwise only fail on the first *failed* delivery.
            // GetService, not GetRequiredService: a publish-only deployment registers no manager.
            settings.Validate(sp.GetService<IOutboxConsumerManager>()?.GetAllConsumerGroupIds());

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
