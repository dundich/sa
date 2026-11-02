using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Poison;
using Sa.Outbox.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PostgreSql.Configuration;

internal sealed class PgOutboxConfiguration(
    IServiceCollection services,
    string providerKey = OutboxScope.DefaultKey) : IPgOutboxConfiguration
{
    public IPgOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null)
    {
        if (configure != null)
        {
            services.AddKeyedSingleton<Action<IServiceProvider, PgOutboxSettings>>(providerKey, configure);
        }

        RegisterOutboxSettings();

        return this;
    }

    public IPgOutboxConfiguration WithDataSource(Action<IDataSourceBuilder>? configure = null)
    {
        // A keyed provider gets its own keyed data source (pool + connection). The default key
        // keeps the classic unkeyed registration, so an application that already registered an
        // NpgsqlDataSource (or configured the unkeyed data source) keeps borrowing it.
        if (string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            services.AddSaPostgreSqlDataSource(configure);
        }
        else
        {
            services.AddSaPostgreSqlDataSource(providerKey, configure);
        }
        return this;
    }

    public IPgOutboxConfiguration WithPolling(Action<IServiceProvider, PgPollingSettings>? configure = null)
    {
        if (configure != null)
        {
            services.AddKeyedSingleton<Action<IServiceProvider, PgPollingSettings>>(providerKey, configure);
        }

        RegisterPollingSettings();

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

    public IPgOutboxConfiguration WithPoisonHandler(Func<IServiceProvider, IPgOutboxPoisonHandler> poisonHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(poisonHandlerFactory);

        services.RemoveAllKeyed<IPgOutboxPoisonHandler>(providerKey);
        services.TryAddKeyedSingleton<IPgOutboxPoisonHandler>(providerKey, (sp, _) => poisonHandlerFactory(sp));
        return this;
    }

    public IPgOutboxConfiguration WithPoisonHandler<TService>(TService instance)
        where TService : class, IPgOutboxPoisonHandler
    {
        ArgumentNullException.ThrowIfNull(instance);

        services.RemoveAllKeyed<IPgOutboxPoisonHandler>(providerKey);
        services.TryAddKeyedSingleton<IPgOutboxPoisonHandler>(providerKey, instance);
        return this;
    }

    public IPgOutboxConfiguration WithPoisonHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IPgOutboxPoisonHandler
    {
        services.RemoveAllKeyed<IPgOutboxPoisonHandler>(providerKey);
        services.TryAddKeyedSingleton<IPgOutboxPoisonHandler, TService>(providerKey);
        return this;
    }

    /// <summary>
    /// Bridges a serializer registered directly in DI under the legacy provider-specific
    /// <see cref="PostgreSql.Serialization.IOutboxMessageSerializer"/> type to the core service the
    /// provider resolves. The default JSON serializer is registered by <c>AddSaOutbox</c>; this only
    /// preserves the old registration style.
    /// </summary>
    internal IPgOutboxConfiguration WithSerializerCompatibilityBridge()
    {
        services.TryAddSingleton<IOutboxMessageSerializer>(static sp =>
            (IOutboxMessageSerializer?)sp.GetService<Sa.Outbox.PostgreSql.Serialization.IOutboxMessageSerializer>()
            ?? JsonOutboxMessageSerializer.Instance);

        return this;
    }

    private void RegisterOutboxSettings()
    {
        services.TryAddKeyedSingleton<PgOutboxSettings>(providerKey, (sp, _) =>
        {
            PgOutboxSettings settings = new();

            var connectionSchema = Sa.Data.PostgreSql.PgDataSourceResolver.TryResolve(sp, providerKey, out IPgDataSource? dataSource)
                ? dataSource!.GetSearchPath()
                : null;

            if (!string.IsNullOrWhiteSpace(connectionSchema))
            {
                settings.TableSettings.WithSchema(connectionSchema);
            }

            var configureActions = sp.GetKeyedServices<Action<IServiceProvider, PgOutboxSettings>>(providerKey);

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

        if (string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            // Backward compatibility: the single-provider API resolves the pg settings without a key.
            services.TryAddSingleton<PgOutboxSettings>(
                sp => sp.GetRequiredKeyedService<PgOutboxSettings>(providerKey));
        }
    }

    private void RegisterComponentSettings()
    {
        services.TryAddKeyedSingleton<PgOutboxTableSettings>(providerKey,
            (sp, _) => sp.GetRequiredKeyedService<PgOutboxSettings>(providerKey).TableSettings);
        services.TryAddKeyedSingleton<PgOutboxMigrationSettings>(providerKey,
            (sp, _) => sp.GetRequiredKeyedService<PgOutboxSettings>(providerKey).MigrationSettings);
        services.TryAddKeyedSingleton<PgOutboxCleanupSettings>(providerKey,
            (sp, _) => sp.GetRequiredKeyedService<PgOutboxSettings>(providerKey).CleanupSettings);
        services.TryAddKeyedSingleton<PgOutboxConsumeSettings>(providerKey,
            (sp, _) => sp.GetRequiredKeyedService<PgOutboxSettings>(providerKey).ConsumeSettings);

        if (string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            services.TryAddSingleton<PgOutboxTableSettings>(
                sp => sp.GetRequiredKeyedService<PgOutboxTableSettings>(providerKey));
            services.TryAddSingleton<PgOutboxMigrationSettings>(
                sp => sp.GetRequiredKeyedService<PgOutboxMigrationSettings>(providerKey));
            services.TryAddSingleton<PgOutboxCleanupSettings>(
                sp => sp.GetRequiredKeyedService<PgOutboxCleanupSettings>(providerKey));
            services.TryAddSingleton<PgOutboxConsumeSettings>(
                sp => sp.GetRequiredKeyedService<PgOutboxConsumeSettings>(providerKey));
        }
    }

    /// <summary>
    /// Registers the polling defaults as their own singleton (not as a part of
    /// <see cref="PgOutboxSettings"/>): the delivery-options provider reads it while a consumer group's
    /// settings are being built, and that happens inside the consumer manager's own factory —
    /// resolving <see cref="PgOutboxSettings"/> from there would ask the manager for its groups while
    /// it is still being constructed. Registered only when <c>WithPolling</c> is called, so a container
    /// that never opts in has no such service and the provider keeps the previous behaviour.
    /// </summary>
    private void RegisterPollingSettings()
    {
        services.TryAddKeyedSingleton(providerKey, (sp, _) =>
        {
            PgPollingSettings settings = new();

            foreach (Action<IServiceProvider, PgPollingSettings> configureAction in
                sp.GetKeyedServices<Action<IServiceProvider, PgPollingSettings>>(providerKey))
            {
                configureAction.Invoke(sp, settings);
            }

            // Validated after every configure action: a profile that leaves an inconsistent pair
            // (idle bound below the base interval) fails at startup instead of on the first group build.
            settings.Validate();

            return settings;
        });

        if (string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            services.TryAddSingleton<PgPollingSettings>(
                sp => sp.GetRequiredKeyedService<PgPollingSettings>(providerKey));
        }
    }
}
