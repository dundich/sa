using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Outbox.Redis.Connection;
using Sa.Outbox.Redis.Poison;
using Sa.Outbox.Serialization;
using StackExchange.Redis;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Redis.Configuration;

/// <summary>
/// Collects the Redis provider configuration and applies it to the container on
/// <see cref="Commit"/>.
/// </summary>
/// <remarks>
/// Registration is deferred on purpose: <see cref="WithEnabled"/> must be able to turn the provider
/// off before anything reaches DI. A disabled provider registers nothing at all — no settings, no
/// connection, no serializer/poison overrides — and the caller skips the core deliveries too.
/// </remarks>
internal sealed class RedisOutboxConfiguration(
    IServiceCollection services,
    string providerKey = OutboxScope.DefaultKey) : IRedisOutboxConfiguration
{
    private readonly List<Action<IServiceProvider, RedisOutboxSettings>> _settingsActions = [];
    private readonly List<Action<OptionsBuilder<RedisOutboxSettings>>> _optionsActions = [];

    private readonly RedisOutboxConnectionOptions _connectionOptions = new();

    private string? _configSectionPath;
    private bool _settingsRequested;
    private bool _connectionRequested;
    private bool _compatibilityBridge;
    private bool _committed;

    private Func<IServiceProvider, IOutboxMessageSerializer>? _serializerFactory;
    private IOutboxMessageSerializer? _serializerInstance;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private Type? _serializerType;

    private Func<IServiceProvider, IRedisOutboxPoisonHandler>? _poisonFactory;
    private IRedisOutboxPoisonHandler? _poisonInstance;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private Type? _poisonType;
    private bool _poisonConfigured;

    /// <summary>The provider key this configuration registers.</summary>
    internal string ProviderKey => providerKey;

    /// <summary>
    /// Whether the provider is registered at all. Default is <see langword="true"/>; a disabled
    /// provider registers nothing, and the one-call form skips the core deliveries as well.
    /// </summary>
    public bool Enabled { get; private set; } = true;

    /// <inheritdoc/>
    public IRedisOutboxConfiguration WithEnabled(bool enabled = true)
    {
        Enabled = enabled;
        return this;
    }

    public IRedisOutboxConfiguration WithConnectionMultiplexer(IConnectionMultiplexer multiplexer)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        return WithConnectionMultiplexer(_ => multiplexer);
    }

    public IRedisOutboxConfiguration WithConnectionMultiplexer(Func<IServiceProvider, IConnectionMultiplexer> multiplexerFactory)
    {
        ArgumentNullException.ThrowIfNull(multiplexerFactory);

        _connectionOptions.DatabaseFactory = null;
        _connectionOptions.MultiplexerFactory = multiplexerFactory;
        return this;
    }

    public IRedisOutboxConfiguration WithDatabase(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return WithDatabase(_ => database);
    }

    public IRedisOutboxConfiguration WithDatabase(Func<IServiceProvider, IDatabase> databaseFactory)
    {
        ArgumentNullException.ThrowIfNull(databaseFactory);

        _connectionOptions.MultiplexerFactory = null;
        _connectionOptions.DatabaseFactory = databaseFactory;
        return this;
    }

    public IRedisOutboxConfiguration WithConnectionString(string connectionStringOrName = "Redis")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrName);
        return WithConnectionString(_ => connectionStringOrName);
    }

    public IRedisOutboxConfiguration WithConnectionString(Func<IServiceProvider, string?> connectionStringOrNameFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionStringOrNameFactory);

        _connectionOptions.ConnectionStringFactory = connectionStringOrNameFactory;
        return this;
    }

    public IRedisOutboxConfiguration WithConnectionOptions(Action<ConfigurationOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        _connectionOptions.ConfigureOptions = configureOptions;
        return this;
    }

    public IRedisOutboxConfiguration WithDatabaseIndex(int databaseIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(databaseIndex);

        _connectionOptions.DatabaseIndex = databaseIndex;
        return this;
    }

    public IRedisOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, RedisOutboxSettings>? configure = null)
    {
        if (configure != null)
        {
            _settingsActions.Add(configure);
        }

        _settingsRequested = true;
        return this;
    }

    public IRedisOutboxConfiguration WithConfiguration(string configSectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        _settingsRequested = true;
        _configSectionPath = configSectionPath;

        return this;
    }

    public IRedisOutboxConfiguration Options(Action<OptionsBuilder<RedisOutboxSettings>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        _settingsRequested = true;
        _optionsActions.Add(configureOptions);

        return this;
    }

    public IRedisOutboxConfiguration WithMessageSerializer(
        Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory)
    {
        ArgumentNullException.ThrowIfNull(messageSerializerFactory);

        _serializerFactory = messageSerializerFactory;
        _serializerInstance = null;
        _serializerType = null;
        return this;
    }

    public IRedisOutboxConfiguration WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer
    {
        ArgumentNullException.ThrowIfNull(instance);

        _serializerFactory = null;
        _serializerInstance = instance;
        _serializerType = null;
        return this;
    }

    public IRedisOutboxConfiguration WithMessageSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IOutboxMessageSerializer
    {
        _serializerFactory = null;
        _serializerInstance = null;
        _serializerType = typeof(TService);
        return this;
    }

    public IRedisOutboxConfiguration WithPoisonHandler(Func<IServiceProvider, IRedisOutboxPoisonHandler> poisonHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(poisonHandlerFactory);

        _poisonFactory = poisonHandlerFactory;
        _poisonInstance = null;
        _poisonType = null;
        _poisonConfigured = true;
        return this;
    }

    public IRedisOutboxConfiguration WithPoisonHandler<TService>(TService instance)
        where TService : class, IRedisOutboxPoisonHandler
    {
        ArgumentNullException.ThrowIfNull(instance);

        _poisonFactory = null;
        _poisonInstance = instance;
        _poisonType = null;
        _poisonConfigured = true;
        return this;
    }

    public IRedisOutboxConfiguration WithPoisonHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IRedisOutboxPoisonHandler
    {
        _poisonFactory = null;
        _poisonInstance = null;
        _poisonType = typeof(TService);
        _poisonConfigured = true;
        return this;
    }

    /// <summary>
    /// Bridges a serializer registered directly in DI under the legacy provider-specific
    /// <see cref="Redis.Serialization.IOutboxMessageSerializer"/> type to the core service the
    /// provider resolves. The default JSON serializer is registered by <c>AddSaOutbox</c>; this only
    /// preserves the old registration style.
    /// </summary>
    internal IRedisOutboxConfiguration WithSerializerCompatibilityBridge()
    {
        _compatibilityBridge = true;
        return this;
    }

    /// <summary>
    /// Records the provider-owned registration of the connection sources. The actual registrations
    /// are applied by <see cref="Commit"/>.
    /// </summary>
    internal IRedisOutboxConfiguration WithConnection()
    {
        _connectionRequested = true;
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
            RegisterOutboxSettings();

        if (_connectionRequested)
            RegisterConnection();

        ApplySerializer();

        if (_poisonConfigured)
        {
            services.RemoveAllKeyed<IRedisOutboxPoisonHandler>(providerKey);

            if (_poisonFactory is not null)
                services.TryAddKeyedSingleton<IRedisOutboxPoisonHandler>(providerKey, (sp, _) => _poisonFactory(sp));
            else if (_poisonInstance is not null)
                services.TryAddKeyedSingleton<IRedisOutboxPoisonHandler>(providerKey, _poisonInstance);
            else if (_poisonType is not null)
                services.TryAddKeyedSingleton(typeof(IRedisOutboxPoisonHandler), providerKey, _poisonType);
        }

        if (_compatibilityBridge)
        {
            services.TryAddSingleton<IOutboxMessageSerializer>(static sp =>
                (IOutboxMessageSerializer?)sp.GetService<Sa.Outbox.Redis.Serialization.IOutboxMessageSerializer>()
                ?? JsonOutboxMessageSerializer.Instance);
        }
    }

    /// <summary>
    /// Registers the serializer per provider. The provider's commands resolve it keyed, so two
    /// providers never overwrite each other's codec; a provider without an explicit serializer
    /// shares the container-level one (the core JSON serializer is the last fallback). The default
    /// key additionally keeps the unkeyed registration for the classic single-provider surface.
    /// </summary>
    private void ApplySerializer()
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

    /// <summary>
    /// Registers the connection sources and the provider that resolves them. Everything else in the
    /// provider depends on the resolved <see cref="IDatabase"/>, so a connection is opened on the
    /// first command, not at container build time.
    /// </summary>
    private void RegisterConnection()
    {
        // Configuration-driven by default: the "Redis" connection string, when present, is the
        // zero-configuration path. A multiplexer or database registered in the container still
        // wins, because the provider checks those before the connection string.
        _connectionOptions.ConnectionStringFactory ??= sp =>
            sp.GetService<IConfiguration>()?.GetConnectionString("Redis");

        // Only an application-registered *unkeyed* database counts as "already provided": the keyed
        // registrations below are the Outbox's own and resolving them here would recurse. The
        // default provider's unkeyed alias is excluded too — see HasApplicationRegisteredDatabase.
        _connectionOptions.UseRegisteredDatabase = HasApplicationRegisteredDatabase();

        services.TryAddKeyedSingleton(providerKey, _connectionOptions);

        services.TryAddKeyedSingleton<IRedisOutboxConnectionProvider>(providerKey, (sp, _) =>
            new RedisOutboxConnectionProvider(
                sp.GetRequiredKeyedService<RedisOutboxConnectionOptions>(providerKey),
                sp));

        services.TryAddKeyedSingleton<IDatabase>(providerKey, (sp, _) =>
            sp.GetRequiredKeyedService<IRedisOutboxConnectionProvider>(providerKey).Database);

        if (string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            // Backward compatibility: single-provider code resolves IDatabase without a key.
            var alias = ServiceDescriptor.Singleton<IDatabase>(
                sp => sp.GetRequiredKeyedService<IDatabase>(providerKey));

            services.TryAdd(alias);

            // Remember the alias so a provider registered later does not mistake it for a database
            // the application provided. Registered even when TryAdd kept an application's own
            // registration: the marker only names a descriptor, it never replaces it.
            services.TryAddSingleton(new OutboxDatabaseAlias(alias));
        }
    }

    /// <summary>
    /// Whether the application registered an unkeyed <see cref="IDatabase"/> itself. The alias the
    /// default provider adds for backward compatibility is excluded: treating it as "already
    /// provided" made a provider registered later borrow the default provider's connection, so its
    /// own connection string was silently ignored.
    /// </summary>
    private bool HasApplicationRegisteredDatabase()
    {
        HashSet<ServiceDescriptor> ownAliases = [.. services
            .Where(static d => d.ServiceType == typeof(OutboxDatabaseAlias))
            .Select(static d => d.ImplementationInstance)
            .OfType<OutboxDatabaseAlias>()
            .Select(static marker => marker.Descriptor)];

        return services.Any(d => d.ServiceType == typeof(IDatabase)
            && !d.IsKeyedService
            && !ownAliases.Contains(d));
    }

    private void RegisterOutboxSettings()
    {
        bool isDefaultKey = string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal);
        string optionsName = isDefaultKey ? Microsoft.Extensions.Options.Options.DefaultName : providerKey;

        OptionsBuilder<RedisOutboxSettings> options = isDefaultKey
            ? services.AddOptions<RedisOutboxSettings>()
            : services.AddOptions<RedisOutboxSettings>(optionsName);

        RedisOutboxSettingsState? state = FindState(services, providerKey);
        if (state is null)
        {
            state = new RedisOutboxSettingsState();
            services.TryAddKeyedSingleton(providerKey, state);
        }

        if (_configSectionPath is not null)
            state.ConfigSectionPath = _configSectionPath;

        // A repeated registration for the same key replaces the callbacks it recorded earlier
        // instead of stacking a second copy: the configurator applies every action of the key, so
        // appending made a re-registered callback run once per registration.
        services.RemoveAllKeyed<Action<IServiceProvider, RedisOutboxSettings>>(providerKey);

        foreach (Action<IServiceProvider, RedisOutboxSettings> configureAction in _settingsActions)
            services.AddKeyedSingleton<Action<IServiceProvider, RedisOutboxSettings>>(providerKey, configureAction);

        // The code actions of WithOutboxSettings and the configuration section are applied by one
        // configurator, in that order. Options(...) actions are registered later on the pipeline by
        // the caller and therefore override both, which is the house precedence order.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<RedisOutboxSettings>, RedisOutboxSettingsConfigurator>());

        options.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RedisOutboxSettings>, RedisOutboxSettingsValidator>());

        // The materialised instance keeps the existing single-settings contract: commands take
        // RedisOutboxSettings directly, and IOptions<T>.Value is cached, so this is the same object
        // for every resolution and every command.
        services.TryAddKeyedSingleton<RedisOutboxSettings>(providerKey, (sp, _) =>
            sp.GetRequiredService<IOptionsMonitor<RedisOutboxSettings>>().Get(optionsName));

        if (isDefaultKey)
        {
            // Backward compatibility: commands used to take the materialised settings unkeyed.
            services.TryAddSingleton<RedisOutboxSettings>(
                sp => sp.GetRequiredKeyedService<RedisOutboxSettings>(providerKey));
        }

        // Recorded actions run last: they see the builder created above and override both the code
        // defaults and the configuration section.
        foreach (Action<OptionsBuilder<RedisOutboxSettings>> configureAction in _optionsActions)
            configureAction(options);
    }

    private static RedisOutboxSettingsState? FindState(IServiceCollection services, string providerKey)
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(RedisOutboxSettingsState)
                && descriptor.ImplementationInstance is RedisOutboxSettingsState state
                && Equals(descriptor.ServiceKey, providerKey))
            {
                return state;
            }
        }

        return null;
    }
}
