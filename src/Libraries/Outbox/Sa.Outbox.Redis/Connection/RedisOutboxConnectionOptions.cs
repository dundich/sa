using StackExchange.Redis;

namespace Sa.Outbox.Redis.Connection;

/// <summary>
/// The connection sources collected by <see cref="Configuration.IRedisOutboxConfiguration"/>.
/// Registered as a singleton and consumed by <see cref="RedisOutboxConnectionProvider"/>, which
/// resolves them in priority order.
/// </summary>
internal sealed class RedisOutboxConnectionOptions
{
    public Func<IServiceProvider, IDatabase>? DatabaseFactory { get; set; }

    public Func<IServiceProvider, IConnectionMultiplexer>? MultiplexerFactory { get; set; }

    public Func<IServiceProvider, string?>? ConnectionStringFactory { get; set; }

    public Action<ConfigurationOptions>? ConfigureOptions { get; set; }

    public int DatabaseIndex { get; set; }

    /// <summary>
    /// Whether an <see cref="IDatabase"/> was already registered in the container when the Outbox
    /// was configured. Checked at configuration time rather than resolution time: asking the
    /// container for <see cref="IDatabase"/> from inside the Outbox's own registration would recurse
    /// into the provider that is being resolved.
    /// </summary>
    public bool UseRegisteredDatabase { get; set; }
}
