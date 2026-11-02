using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Connection;

/// <summary>
/// Resolves the Outbox connection once, on first use, in this order:
/// explicit <see cref="IDatabase"/> → explicit <see cref="IConnectionMultiplexer"/> →
/// <see cref="IDatabase"/>/<see cref="IConnectionMultiplexer"/> already registered in the container →
/// a connection string (explicit or from <c>IConfiguration.GetConnectionString</c>).
/// </summary>
/// <remarks>
/// <para>
/// Only the connection-string path creates the multiplexer, and only that path disposes it. A
/// multiplexer handed in by the application is shared with whatever else uses it, so disposing it
/// here would tear down unrelated code on container shutdown.
/// </para>
/// <para>
/// The resolution is lazy on purpose: a deployment that only publishes builds the whole provider
/// graph without ever touching the network, and a container that registers the provider but never
/// publishes (a consumer-only process with no delivery jobs) stays offline.
/// </para>
/// </remarks>
internal sealed class RedisOutboxConnectionProvider : IRedisOutboxConnectionProvider, IAsyncDisposable, IDisposable
{
    private readonly RedisOutboxConnectionOptions _options;
    private readonly IServiceProvider _services;
    private readonly Lazy<Resolved> _connection;
    private bool _disposed;

    public RedisOutboxConnectionProvider(RedisOutboxConnectionOptions options, IServiceProvider services)
    {
        _options = options;
        _services = services;

        // ExecutionAndPublication: one connection, even under concurrent first use.
        _connection = new Lazy<Resolved>(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IDatabase Database => _connection.Value.Database;

    public bool IsOwner => _connection.IsValueCreated && _connection.Value.Owned is not null;

    private Resolved Resolve()
    {
        if (_options.DatabaseFactory is { } databaseFactory)
            return new Resolved(databaseFactory(_services), owned: null);

        if (_options.MultiplexerFactory is { } multiplexerFactory)
            return new Resolved(multiplexerFactory(_services).GetDatabase(_options.DatabaseIndex), owned: null);

        if (_options.UseRegisteredDatabase && _services.GetService<IDatabase>() is { } registeredDatabase)
            return new Resolved(registeredDatabase, owned: null);

        if (_services.GetService<IConnectionMultiplexer>() is { } registeredMultiplexer)
            return new Resolved(registeredMultiplexer.GetDatabase(_options.DatabaseIndex), owned: null);

        string? connectionString = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The Redis Outbox has no connection source. Register an IConnectionMultiplexer/IDatabase in the " +
                "container, or call WithConnectionMultiplexer/WithDatabase/WithConnectionString on " +
                "AddSaOutboxUsingRedis.");
        }

        ConfigurationOptions configurationOptions = ConfigurationOptions.Parse(connectionString);

        // A background worker must not fail to start because Redis was momentarily unavailable:
        // StackExchange.Redis reconnects in the background, the first command just waits.
        configurationOptions.AbortOnConnectFail = false;
        _options.ConfigureOptions?.Invoke(configurationOptions);

        ConnectionMultiplexer multiplexer = ConnectionMultiplexer.Connect(configurationOptions);
        return new Resolved(multiplexer.GetDatabase(_options.DatabaseIndex), owned: multiplexer);
    }

    private string? ResolveConnectionString()
    {
        string? value = _options.ConnectionStringFactory?.Invoke(_services);

        if (string.IsNullOrWhiteSpace(value))
            return null;

        // A name without a connection-string shape is a configuration key, not a server address.
        if (value.Contains('=', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal))
            return value;

        return _services.GetService<IConfiguration>()?.GetConnectionString(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_connection.IsValueCreated)
            _connection.Value.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Resolved(IDatabase database, ConnectionMultiplexer? owned) : IDisposable
    {
        public IDatabase Database { get; } = database;

        public ConnectionMultiplexer? Owned { get; } = owned;

        public void Dispose() => Owned?.Dispose();
    }
}
