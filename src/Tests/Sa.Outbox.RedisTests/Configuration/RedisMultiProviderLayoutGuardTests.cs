using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox.Redis;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Services.Plug;

namespace Sa.Outbox.RedisTests.Configuration;

/// <summary>
/// The Redis multi-provider layout guard: two providers with the same endpoint, database index and
/// key prefix would share their streams, so the host start fails; a different prefix, a different
/// database or a different endpoint passes. No server is contacted.
/// </summary>
public class RedisMultiProviderLayoutGuardTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(
        (string Key, string ConnectionString, string Prefix, int DatabaseIndex) a,
        (string Key, string ConnectionString, string Prefix, int DatabaseIndex)? b = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddSaOutboxUsingRedis(Configure(a.ConnectionString, a.Prefix, a.DatabaseIndex), configureOutbox: null, a.Key);

        if (b is { } other)
        {
            services.AddSaOutboxUsingRedis(
                Configure(other.ConnectionString, other.Prefix, other.DatabaseIndex),
                configureOutbox: null,
                other.Key);
        }

        return services.BuildServiceProvider();

        static Action<IRedisOutboxConfiguration> Configure(string connectionString, string prefix, int databaseIndex)
            => redis => redis
                .WithConnectionString(connectionString)
                .WithDatabaseIndex(databaseIndex)
                .WithOutboxSettings((_, settings) => settings.KeySettings.Prefix = prefix);
    }

    private static async Task StartGuard(ServiceProvider sp)
    {
        RedisMultiProviderGuard guard = Assert.Single(
            sp.GetServices<IHostedService>().OfType<RedisMultiProviderGuard>());

        await guard.StartAsync(TestToken);
    }

    [Fact]
    public async Task SameEndpointDatabaseAndPrefix_FailsTheStart()
    {
        using ServiceProvider sp = Build(
            ("a", "localhost:6379", "shared", 0),
            ("b", "localhost:6379", "shared", 0));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartGuard(sp));

        Assert.Contains("deliver every message twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentPrefixes_AreIndependent()
    {
        using ServiceProvider sp = Build(
            ("a", "localhost:6379", "outbox_a", 0),
            ("b", "localhost:6379", "outbox_b", 0));

        await StartGuard(sp); // does not throw
    }

    [Fact]
    public async Task DifferentDatabaseIndexes_AreIndependent()
    {
        using ServiceProvider sp = Build(
            ("a", "localhost:6379", "shared", 0),
            ("b", "localhost:6379", "shared", 1));

        await StartGuard(sp); // does not throw
    }

    [Fact]
    public async Task DifferentEndpoints_AreIndependent()
    {
        using ServiceProvider sp = Build(
            ("a", "redis-a:6379", "shared", 0),
            ("b", "redis-b:6379", "shared", 0));

        await StartGuard(sp); // does not throw
    }

    [Fact]
    public async Task SingleProvider_Passes()
    {
        using ServiceProvider sp = Build(("outbox", "localhost:6379", "sa:outbox", 0));

        await StartGuard(sp); // does not throw
    }

    [Fact]
    public async Task CustomMultiplexer_SkipsTheComparison()
    {
        // A multiplexer factory hides the endpoint until it runs; the guard cannot compare it and
        // must not reject a valid setup. The factory is never invoked by the guard.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services
            .AddSaOutboxUsingRedis(
                redis => redis.WithConnectionMultiplexer(_ => null!)
                              .WithOutboxSettings((_, settings) => settings.KeySettings.Prefix = "shared"),
                configureOutbox: null,
                "a")
            .AddSaOutboxUsingRedis(
                redis => redis.WithConnectionMultiplexer(_ => null!)
                              .WithOutboxSettings((_, settings) => settings.KeySettings.Prefix = "shared"),
                configureOutbox: null,
                "b");

        using ServiceProvider sp = services.BuildServiceProvider();

        await StartGuard(sp); // does not throw
    }
}
