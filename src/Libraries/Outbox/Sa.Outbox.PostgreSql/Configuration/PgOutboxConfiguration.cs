using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Poison;
using Sa.Outbox.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Collects the PostgreSQL provider configuration and applies it to the container on
/// <see cref="Commit"/>.
/// </summary>
/// <remarks>
/// Registration is deferred on purpose: <see cref="WithEnabled"/> must be able to turn the provider
/// off before anything reaches DI. A disabled provider registers nothing at all — no settings, no
/// keyed data source (whose <c>ValidateOnStart</c> would otherwise fail on an empty connection
/// string), no serializer/poison overrides, and the caller skips the core deliveries too.
/// </remarks>
internal sealed class PgOutboxConfiguration(
    IServiceCollection services,
    string providerKey = OutboxScope.DefaultKey) : IPgOutboxConfiguration
{
    private readonly List<Action<IServiceProvider, PgOutboxSettings>> _settingsActions = [];
    private readonly List<Action<IServiceProvider, PgPollingSettings>> _pollingActions = [];

    private Action<IDataSourceBuilder>? _dataSourceConfigure;
    private bool _dataSourceRequested;
    private bool _settingsRequested;
    private bool _pollingRequested;
    private bool _compatibilityBridge;

    private Func<IServiceProvider, IOutboxMessageSerializer>? _serializerFactory;
    private IOutboxMessageSerializer? _serializerInstance;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private Type? _serializerType;

    private Func<IServiceProvider, IPgOutboxPoisonHandler>? _poisonFactory;
    private IPgOutboxPoisonHandler? _poisonInstance;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private Type? _poisonType;
    private bool _poisonConfigured;

    private bool _committed;

    /// <summary>The provider key this configuration registers.</summary>
    internal string ProviderKey => providerKey;

    /// <summary>
    /// Whether the provider is registered at all. Default is <see langword="true"/>; a disabled
    /// provider registers nothing, and the one-call form skips the core deliveries as well.
    /// </summary>
    public bool Enabled { get; private set; } = true;

    /// <inheritdoc/>
    public IPgOutboxConfiguration WithEnabled(bool enabled = true)
    {
        Enabled = enabled;
        return this;
    }

    public IPgOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null)
    {
        if (configure != null)
        {
            _settingsActions.Add(configure);
        }

        _settingsRequested = true;
        return this;
    }

    public IPgOutboxConfiguration WithDataSource(Action<IDataSourceBuilder>? configure = null)
    {
        if (configure != null)
        {
            _dataSourceConfigure = configure;
        }

        _dataSourceRequested = true;
        return this;
    }

    public IPgOutboxConfiguration WithPolling(Action<IServiceProvider, PgPollingSettings>? configure = null)
    {
        if (configure != null)
        {
            _pollingActions.Add(configure);
        }

        _pollingRequested = true;
        return this;
    }

    public IPgOutboxConfiguration WithMessageSerializer(
        Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory)
    {
        ArgumentNullException.ThrowIfNull(messageSerializerFactory);

        _serializerFactory = messageSerializerFactory;
        _serializerInstance = null;
        _serializerType = null;
        return this;
    }

    public IPgOutboxConfiguration WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer
    {
        ArgumentNullException.ThrowIfNull(instance);

        _serializerFactory = null;
        _serializerInstance = instance;
        _serializerType = null;
        return this;
    }

    public IPgOutboxConfiguration WithMessageSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IOutboxMessageSerializer
    {
        _serializerFactory = null;
        _serializerInstance = null;
        _serializerType = typeof(TService);
        return this;
    }

    public IPgOutboxConfiguration WithPoisonHandler(Func<IServiceProvider, IPgOutboxPoisonHandler> poisonHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(poisonHandlerFactory);

        _poisonFactory = poisonHandlerFactory;
        _poisonInstance = null;
        _poisonType = null;
        _poisonConfigured = true;
        return this;
    }

    public IPgOutboxConfiguration WithPoisonHandler<TService>(TService instance)
        where TService : class, IPgOutboxPoisonHandler
    {
        ArgumentNullException.ThrowIfNull(instance);

        _poisonFactory = null;
        _poisonInstance = instance;
        _poisonType = null;
        _poisonConfigured = true;
        return this;
    }

    public IPgOutboxConfiguration WithPoisonHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IPgOutboxPoisonHandler
    {
        _poisonFactory = null;
        _poisonInstance = null;
        _poisonType = typeof(TService);
        _poisonConfigured = true;
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
        _compatibilityBridge = true;
        return this;
    }

    /// <summary>
    /// Applies every recorded intent to the container. Idempotent; the root setup calls it only when
    /// <see cref="Enabled"/> is true. Direct users of this type (tests) call it themselves.
    /// </summary>
    internal void Commit()
    {
        if (_committed) return;
        _committed = true;

        if (_settingsRequested)
        {
            // A repeated registration for the same key replaces the callbacks it recorded earlier
            // instead of stacking a second copy: the settings factory applies every action of the
            // key, so appending made a re-registered callback run once per registration.
            services.RemoveAllKeyed<Action<IServiceProvider, PgOutboxSettings>>(providerKey);

            foreach (Action<IServiceProvider, PgOutboxSettings> action in _settingsActions)
                services.AddKeyedSingleton<Action<IServiceProvider, PgOutboxSettings>>(providerKey, action);

            RegisterOutboxSettings();
        }

        if (_pollingRequested)
        {
            services.RemoveAllKeyed<Action<IServiceProvider, PgPollingSettings>>(providerKey);

            foreach (Action<IServiceProvider, PgPollingSettings> action in _pollingActions)
                services.AddKeyedSingleton<Action<IServiceProvider, PgPollingSettings>>(providerKey, action);

            RegisterPollingSettings();
        }

        if (_dataSourceRequested)
            RegisterDataSource();

        RegisterSerializer();

        if (_poisonConfigured)
        {
            services.RemoveAllKeyed<IPgOutboxPoisonHandler>(providerKey);

            if (_poisonFactory is not null)
                services.TryAddKeyedSingleton<IPgOutboxPoisonHandler>(providerKey, (sp, _) => _poisonFactory(sp));
            else if (_poisonInstance is not null)
                services.TryAddKeyedSingleton<IPgOutboxPoisonHandler>(providerKey, _poisonInstance);
            else if (_poisonType is not null)
                services.TryAddKeyedSingleton(typeof(IPgOutboxPoisonHandler), providerKey, _poisonType);
        }

        if (_compatibilityBridge)
        {
#pragma warning disable CS0618 // The bridge exists precisely to read the legacy provider-local alias.
            services.TryAddSingleton<IOutboxMessageSerializer>(static sp =>
                (IOutboxMessageSerializer?)sp.GetService<Sa.Outbox.PostgreSql.Serialization.IOutboxMessageSerializer>()
                ?? JsonOutboxMessageSerializer.Instance);
#pragma warning restore CS0618
        }
    }

    /// <summary>
    /// Registers the provider's data source unless the key already has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A repeated registration of the same outbox key is tolerated and must not re-register the data
    /// source: the first registration owns the connection, and passing the key to
    /// <c>AddSaPostgreSqlDataSource</c> a second time would be rejected as a duplicate
    /// configuration. <see cref="PgOutboxDataSourceMarker"/> records what this provider owns.
    /// </para>
    /// <para>
    /// A data source another module registered under the same key is a different case. A bare
    /// <c>WithDataSource()</c> only asks for a data source to exist, so that one is borrowed — the
    /// shared key is the point. A <c>WithDataSource(configure)</c> call is an explicit connection
    /// intent: it is applied to the key, and if that module already configured it,
    /// <c>AddSaPostgreSqlDataSource</c> fails with its own "already configured" message instead of
    /// silently dropping this connection string.
    /// </para>
    /// </remarks>
    private void RegisterDataSource()
    {
        // The default provider registers the unnamed ("") data source, a keyed one uses its scope.
        string dataSourceKey = string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal)
            ? string.Empty
            : providerKey;

        if (IsOutboxDataSourceRegistered(dataSourceKey)) return;

        if (_dataSourceConfigure is null && HasDataSource(dataSourceKey)) return;

        // A keyed provider gets its own keyed data source (pool + connection). The default key
        // keeps the classic unkeyed registration, so an application that already registered an
        // NpgsqlDataSource (or configured the unkeyed data source) keeps borrowing it.
        if (string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
            services.AddSaPostgreSqlDataSource(_dataSourceConfigure);
        else
            services.AddSaPostgreSqlDataSource(providerKey, _dataSourceConfigure);

        services.TryAddKeyedSingleton(dataSourceKey, new PgOutboxDataSourceMarker());
    }

    /// <summary>Whether an earlier outbox registration for this key already registered its data source.</summary>
    private bool IsOutboxDataSourceRegistered(string dataSourceKey)
        => services.Any(d => d.ServiceType == typeof(PgOutboxDataSourceMarker)
            && Equals(d.ServiceKey, dataSourceKey));

    /// <summary>Whether any keyed data source exists for the key, outbox-owned or not.</summary>
    private bool HasDataSource(string dataSourceKey)
        => services.Any(d => d.ServiceType == typeof(IPgDataSource)
            && d.IsKeyedService
            && Equals(d.ServiceKey, dataSourceKey));

    /// <summary>
    /// Registers the serializer per provider. The provider's commands resolve it keyed, so two
    /// providers never overwrite each other's codec; a provider without an explicit serializer
    /// shares the container-level one (the core JSON serializer is the last fallback). The default
    /// key additionally keeps the unkeyed registration for the classic single-provider surface.
    /// </summary>
    private void RegisterSerializer()
    {
        bool isDefaultKey = string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal);

        if (_serializerFactory is not null)
        {
            services.RemoveAllKeyed<IOutboxMessageSerializer>(providerKey);
            services.TryAddKeyedSingleton<IOutboxMessageSerializer>(providerKey, (sp, _) => _serializerFactory(sp));

            if (isDefaultKey)
            {
                services.RemoveAll<IOutboxMessageSerializer>();
                services.TryAddSingleton<IOutboxMessageSerializer>(_serializerFactory);
            }
        }
        else if (_serializerInstance is not null)
        {
            services.RemoveAllKeyed<IOutboxMessageSerializer>(providerKey);
            services.TryAddKeyedSingleton<IOutboxMessageSerializer>(providerKey, _serializerInstance);

            if (isDefaultKey)
            {
                services.RemoveAll<IOutboxMessageSerializer>();
                services.TryAddSingleton<IOutboxMessageSerializer>(_serializerInstance);
            }
        }
        else if (_serializerType is not null)
        {
            services.RemoveAllKeyed<IOutboxMessageSerializer>(providerKey);
            services.TryAddKeyedSingleton(typeof(IOutboxMessageSerializer), providerKey, _serializerType);

            if (isDefaultKey)
            {
                services.RemoveAll<IOutboxMessageSerializer>();
                services.TryAddSingleton(typeof(IOutboxMessageSerializer), _serializerType);
            }
        }

        // Registered after the explicit case: TryAddKeyed keeps an explicit serializer above, and a
        // provider without one follows the container-level registration.
        services.TryAddKeyedSingleton<IOutboxMessageSerializer>(providerKey, static (sp, _) =>
            sp.GetService<IOutboxMessageSerializer>() ?? JsonOutboxMessageSerializer.Instance);
    }

    private void RegisterOutboxSettings()
    {
        services.TryAddKeyedSingleton<PgOutboxSettings>(providerKey, (sp, _) =>
        {
            PgOutboxSettings settings = new();

            var connectionSchema = PgDataSourceResolver.TryResolve(sp, providerKey, out IPgDataSource? dataSource)
                ? dataSource!.GetSearchPath()
                : null;

            if (!string.IsNullOrWhiteSpace(connectionSchema))
            {
                settings.TableSettings.WithSchema(connectionSchema);
            }

            foreach (var configureAction in sp.GetKeyedServices<Action<IServiceProvider, PgOutboxSettings>>(providerKey))
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

/// <summary>
/// Records that the outbox provider registered the PostgreSQL data source for a key. The key is
/// carried as the marker's <see cref="ServiceDescriptor.ServiceKey"/>, so a marker for one scope
/// does not hide another scope's own data source. Used to tell "this provider already owns the
/// connection" apart from "another module registered one for the same key".
/// </summary>
internal sealed class PgOutboxDataSourceMarker
{
}
