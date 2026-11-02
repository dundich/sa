using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Outbox.Redis.Connection;
using Sa.Outbox.Redis.Poison;
using Sa.Outbox.Redis.Serialization;
using StackExchange.Redis;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Redis.Configuration;

internal sealed class RedisOutboxConfiguration(IServiceCollection services) : IRedisOutboxConfiguration
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
            services.AddSingleton(configure);
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

        services.RemoveAll<IRedisOutboxPoisonHandler>();
        services.TryAddSingleton<IRedisOutboxPoisonHandler>(poisonHandlerFactory);
        return this;
    }

    public IRedisOutboxConfiguration WithPoisonHandler<TService>(TService instance)
        where TService : class, IRedisOutboxPoisonHandler
    {
        ArgumentNullException.ThrowIfNull(instance);

        services.RemoveAll<IRedisOutboxPoisonHandler>();
        services.TryAddSingleton<IRedisOutboxPoisonHandler>(instance);
        return this;
    }

    public IRedisOutboxConfiguration WithPoisonHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IRedisOutboxPoisonHandler
    {
        services.RemoveAll<IRedisOutboxPoisonHandler>();
        services.TryAddSingleton<IRedisOutboxPoisonHandler, TService>();
        return this;
    }

    internal IRedisOutboxConfiguration WithDefaultSerializer()
    {
        services.TryAddSingleton<IOutboxMessageSerializer>(OutboxMessageSerializer.Instance);
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
        _connectionOptions.UseRegisteredDatabase = services.Any(d => d.ServiceType == typeof(IDatabase));

        services.TryAddSingleton(_connectionOptions);

        services.TryAddSingleton<IRedisOutboxConnectionProvider>(sp =>
            new RedisOutboxConnectionProvider(
                sp.GetRequiredService<RedisOutboxConnectionOptions>(),
                sp));

        services.TryAddSingleton<IDatabase>(sp =>
            sp.GetRequiredService<IRedisOutboxConnectionProvider>().Database);

        return this;
    }

    private void RegisterOutboxSettings()
    {
        if (_optionsBuilder is not null)
            return;

        OptionsBuilder<RedisOutboxSettings> options = services.AddOptions<RedisOutboxSettings>();

        RedisOutboxSettingsState? state = FindState(services);
        if (state is null)
        {
            state = new RedisOutboxSettingsState();
            services.AddSingleton(state);
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
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<RedisOutboxSettings>>().Value);

        _optionsBuilder = options;
    }

    private static RedisOutboxSettingsState? FindState(IServiceCollection services)
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(RedisOutboxSettingsState)
                && descriptor.ImplementationInstance is RedisOutboxSettingsState state)
            {
                return state;
            }
        }

        return null;
    }
}
