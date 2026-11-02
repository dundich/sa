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

internal sealed class RedisOutboxConfiguration(
    IServiceCollection services,
    string providerKey = OutboxScope.DefaultKey) : IRedisOutboxConfiguration
{
    /// <summary>
    /// The <see cref="OptionsBuilder{TOptions}"/> of the provider, created once by the first
    /// <see cref="WithOutboxSettings"/> call so that <see cref="WithConfiguration"/> and
    /// <see cref="Options"/> always find it.
    /// </summary>
    private OptionsBuilder<RedisOutboxSettings>? _optionsBuilder;

    /// <summary>The mutable state the configurator reads at options-build time.</summary>
    private RedisOutboxSettingsState _optionsState = null!;

    /// <summary>
    /// The connection sources collected by this builder. One instance for the whole configuration
    /// chain, so a later <c>WithDatabase</c>/<c>WithConnectionString</c> simply replaces the earlier
    /// choice.
    /// </summary>
    private readonly RedisOutboxConnectionOptions _connectionOptions = new();

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
            services.AddKeyedSingleton<Action<IServiceProvider, RedisOutboxSettings>>(providerKey, configure);
        }

        RegisterOutboxSettings();

        return this;
    }

    public IRedisOutboxConfiguration WithConfiguration(string configSectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        RegisterOutboxSettings();
        _optionsState.ConfigSectionPath = configSectionPath;

        return this;
    }

    public IRedisOutboxConfiguration Options(Action<OptionsBuilder<RedisOutboxSettings>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        RegisterOutboxSettings();
        configureOptions(_optionsBuilder!);

        return this;
    }

    public IRedisOutboxConfiguration WithMessageSerializer(
        Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory)
    {
        ArgumentNullException.ThrowIfNull(messageSerializerFactory);

        services.RemoveAll<IOutboxMessageSerializer>();
        services.TryAddSingleton<IOutboxMessageSerializer>(messageSerializerFactory);
        return this;
    }

    public IRedisOutboxConfiguration WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer
    {
        ArgumentNullException.ThrowIfNull(instance);

        services.RemoveAll<IOutboxMessageSerializer>();
        services.TryAddSingleton<IOutboxMessageSerializer>(instance);
        return this;
    }

    public IRedisOutboxConfiguration WithMessageSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IOutboxMessageSerializer
    {
        services.RemoveAll<IOutboxMessageSerializer>();
        services.TryAddSingleton<IOutboxMessageSerializer, TService>();
        return this;
    }

    public IRedisOutboxConfiguration WithPoisonHandler(Func<IServiceProvider, IRedisOutboxPoisonHandler> poisonHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(poisonHandlerFactory);

        services.RemoveAllKeyed<IRedisOutboxPoisonHandler>(providerKey);
        services.TryAddKeyedSingleton<IRedisOutboxPoisonHandler>(providerKey, (sp, _) => poisonHandlerFactory(sp));
        return this;
    }

    public IRedisOutboxConfiguration WithPoisonHandler<TService>(TService instance)
        where TService : class, IRedisOutboxPoisonHandler
    {
        ArgumentNullException.ThrowIfNull(instance);

        services.RemoveAllKeyed<IRedisOutboxPoisonHandler>(providerKey);
        services.TryAddKeyedSingleton<IRedisOutboxPoisonHandler>(providerKey, instance);
        return this;
    }

    public IRedisOutboxConfiguration WithPoisonHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IRedisOutboxPoisonHandler
    {
        services.RemoveAllKeyed<IRedisOutboxPoisonHandler>(providerKey);
        services.TryAddKeyedSingleton<IRedisOutboxPoisonHandler, TService>(providerKey);
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
        services.TryAddSingleton<IOutboxMessageSerializer>(static sp =>
            (IOutboxMessageSerializer?)sp.GetService<Sa.Outbox.Redis.Serialization.IOutboxMessageSerializer>()
            ?? JsonOutboxMessageSerializer.Instance);

        return this;
    }

    /// <summary>
    /// Registers the connection sources and the provider that resolves them. Everything else in the
    /// provider depends on the resolved <see cref="IDatabase"/>, so a connection is opened on the
    /// first command, not at container build time.
    /// </summary>
    internal IRedisOutboxConfiguration WithConnection()
    {
        // Configuration-driven by default: the "Redis" connection string, when present, is the
        // zero-configuration path. A multiplexer or database registered in the container still
        // wins, because the provider checks those before the connection string.
        _connectionOptions.ConnectionStringFactory ??= sp =>
            sp.GetService<IConfiguration>()?.GetConnectionString("Redis");

        // Recorded before the Outbox adds its own IDatabase: from then on a non-empty registration
        // may well be the Outbox's own factory, and resolving it from the provider would recurse.
        // Only an application-registered *unkeyed* database counts as "already provided": the
        // keyed registrations below are the Outbox's own and resolving them here would recurse.
        _connectionOptions.UseRegisteredDatabase = services.Any(
            d => d.ServiceType == typeof(IDatabase) && !d.IsKeyedService);

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
            services.TryAddSingleton<IDatabase>(
                sp => sp.GetRequiredKeyedService<IDatabase>(providerKey));
        }

        return this;
    }

    private void RegisterOutboxSettings()
    {
        if (_optionsBuilder is not null)
            return;

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

        _optionsState = state;

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

        _optionsBuilder = options;
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
