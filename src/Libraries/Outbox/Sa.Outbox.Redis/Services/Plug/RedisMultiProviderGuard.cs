using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Connection;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Services.Plug;

/// <summary>
/// Fails the host start when two Redis outbox providers share one storage layout: the same endpoint,
/// logical database and key prefix. Such providers would append to and consume from the same
/// streams, so every message would be delivered twice.
/// </summary>
/// <remarks>
/// <para>
/// The layout is <c>(endpoint, database index, prefix, separator)</c>. Only providers configured
/// through a connection string can be compared — that string is resolved without opening a
/// connection. A provider fed by an explicit <see cref="IDatabase"/> or
/// <see cref="IConnectionMultiplexer"/> factory is skipped: its endpoint is not knowable before the
/// factory runs, and a wrong guess would reject a valid setup.
/// </para>
/// <para>
/// Two providers in the same database are fine as long as their prefixes differ; two providers on
/// different databases are independent even with the same prefix.
/// </para>
/// </remarks>
internal sealed class RedisMultiProviderGuard(
    IEnumerable<RedisProviderLayout> layouts,
    IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> seen = new(StringComparer.Ordinal);

        foreach (RedisProviderLayout layout in layouts)
        {
            string? fingerprint = DescribeLayout(layout.ProviderKey);

            // Unknown connection source (a multiplexer/database factory): nothing to compare.
            if (fingerprint is null) continue;

            if (seen.TryGetValue(fingerprint, out string? other))
            {
                throw new InvalidOperationException(
                    $"Redis outbox providers '{other}' and '{layout.ProviderKey}' point at the same " +
                    "storage (same endpoint, database index and key prefix). Both would append to and " +
                    "consume from the same streams and deliver every message twice. Give one of them " +
                    "its own key prefix via WithOutboxSettings, or use another database.");
            }

            seen[fingerprint] = layout.ProviderKey;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string? DescribeLayout(string providerKey)
    {
        RedisOutboxConnectionOptions connection =
            services.GetRequiredKeyedService<RedisOutboxConnectionOptions>(providerKey);

        // Only the connection-string path is knowable before it runs: an explicit database or
        // multiplexer (or one borrowed from the container) hides the endpoint, and comparing the
        // configuration string instead of the real connection could reject a valid setup.
        if (connection.DatabaseFactory is not null
            || connection.MultiplexerFactory is not null
            || connection.UseRegisteredDatabase)
        {
            return null;
        }

        if (connection.ConnectionStringFactory is not { } connectionStringFactory)
            return null;

        string? raw = connectionStringFactory(services);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string? connectionString = raw.Contains('=', StringComparison.Ordinal)
                                   || raw.Contains(':', StringComparison.Ordinal)
            ? raw
            : services.GetService<IConfiguration>()?.GetConnectionString(raw);

        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        RedisOutboxSettings settings =
            services.GetRequiredKeyedService<RedisOutboxSettings>(providerKey);

        return string.Join(
            '|',
            Endpoints(connectionString),
            connection.DatabaseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.KeySettings.Prefix,
            settings.KeySettings.Separator);
    }

    /// <summary>
    /// The endpoint list of a connection string, without opening a connection. Falls back to the raw
    /// string when StackExchange.Redis cannot parse it (it still identifies the layout).
    /// </summary>
    private static string Endpoints(string connectionString)
    {
        try
        {
            ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);

            return string.Join(
                ',',
                options.EndPoints
                    .Select(static endpoint => endpoint.ToString())
                    .Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return connectionString.Trim();
        }
    }
}
