using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Connection;
using StackExchange.Redis;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// Keyed multi-provider for Redis: two providers in one container get their own keyed settings,
/// keyed prefixes (so their streams never collide) and their own provider registry. Registration
/// level only — no Redis server is contacted.
/// </summary>
public class MultiProviderRedisTests
{
    internal sealed class TestMessage
    {
        public string PayloadId { get; set; } = string.Empty;
        public int TenantId { get; set; } = 1;
    }

    internal sealed class TestConsumer : IConsumer<TestMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services
            .AddSaOutboxUsingRedis(
                cfg => cfg.WithConnectionString("localhost:6379"),
                outbox => outbox.WithDeliveries(deliveries => deliveries
                    .AddDelivery<TestConsumer, TestMessage>("rp_a_group")),
                "rp-a")
            .AddSaOutboxUsingRedis(
                cfg => cfg.WithConnectionString("localhost:6379"),
                outbox => outbox.WithDeliveries(deliveries => deliveries
                    .AddDelivery<TestConsumer, TestMessage>("rp_b_group")),
                "rp-b");

        return services.BuildServiceProvider();
    }

    [Fact]
    public void EachProvider_GetsItsOwnPrefixedSettings()
    {
        using ServiceProvider sp = Build();

        RedisOutboxSettings a = sp.GetRequiredKeyedService<RedisOutboxSettings>("rp-a");
        RedisOutboxSettings b = sp.GetRequiredKeyedService<RedisOutboxSettings>("rp-b");

        // The default prefix is shared storage; a non-default provider gets its own suffix.
        Assert.Equal($"{RedisOutboxKeySettings.Defaults.Prefix}:rp-a", a.KeySettings.Prefix);
        Assert.Equal($"{RedisOutboxKeySettings.Defaults.Prefix}:rp-b", b.KeySettings.Prefix);
        Assert.NotEqual(a.KeySettings.StreamKey(1, "part", "TestMessage"), b.KeySettings.StreamKey(1, "part", "TestMessage"));
    }

    [Fact]
    public void DefaultProvider_KeepsTheClassicPrefix()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSaOutboxUsingRedis(cfg => cfg.WithConnectionString("localhost:6379"));

        using ServiceProvider sp = services.BuildServiceProvider();

        // Single-provider applications keep the unkeyed settings and the classic key prefix.
        RedisOutboxSettings settings = sp.GetRequiredService<RedisOutboxSettings>();
        Assert.Equal(RedisOutboxKeySettings.Defaults.Prefix, settings.KeySettings.Prefix);
    }

    [Fact]
    public void EachProvider_HasItsOwnKeyedRegistry()
    {
        using ServiceProvider sp = Build();

        IOutboxProviderRegistry a = sp.GetRequiredKeyedService<IOutboxProviderRegistry>("rp-a");
        IOutboxProviderRegistry b = sp.GetRequiredKeyedService<IOutboxProviderRegistry>("rp-b");

        Assert.Equal("Redis", a.Descriptor.Name);
        Assert.Equal("Redis", b.Descriptor.Name);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void KeyedProvider_RegisteredAfterTheDefault_DoesNotBorrowItsDatabase()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services
            .AddSaOutboxUsingRedis(
                cfg => cfg.WithConnectionString("localhost:6379"),
                outbox => outbox.WithDeliveries(d => d.AddDelivery<TestConsumer, TestMessage>("dp_default_group")))
            .AddSaOutboxUsingRedis(
                cfg => cfg.WithConnectionString("localhost:6380"),
                outbox => outbox.WithDeliveries(d => d.AddDelivery<TestConsumer, TestMessage>("dp_keyed_group")),
                "dp-keyed");

        using ServiceProvider sp = services.BuildServiceProvider();

        // The default provider registers an unkeyed IDatabase alias for backward compatibility; the
        // keyed provider must not mistake it for an application-provided database, otherwise its own
        // connection string would be silently ignored.
        Assert.False(
            sp.GetRequiredKeyedService<RedisOutboxConnectionOptions>("dp-keyed").UseRegisteredDatabase);
        Assert.False(
            sp.GetRequiredKeyedService<RedisOutboxConnectionOptions>(OutboxScope.DefaultKey).UseRegisteredDatabase);
    }

    [Fact]
    public void ApplicationRegisteredDatabase_IsStillBorrowed()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(DispatchProxy.Create<IDatabase, NoopProxy>());

        services
            .AddSaOutboxUsingRedis(
                cfg => cfg.WithConnectionString("localhost:6379"),
                outbox => outbox.WithDeliveries(d => d.AddDelivery<TestConsumer, TestMessage>("ad_default_group")))
            .AddSaOutboxUsingRedis(
                cfg => cfg.WithConnectionString("localhost:6380"),
                outbox => outbox.WithDeliveries(d => d.AddDelivery<TestConsumer, TestMessage>("ad_keyed_group")),
                "ad-keyed");

        using ServiceProvider sp = services.BuildServiceProvider();

        // An application-owned database wins for every provider, as documented.
        Assert.True(
            sp.GetRequiredKeyedService<RedisOutboxConnectionOptions>("ad-keyed").UseRegisteredDatabase);
    }

    public class NoopProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    [Fact]
    public void ConsumerGroups_AreBoundToTheirProviderKey()
    {
        using ServiceProvider sp = Build();
        var manager = sp.GetRequiredService<IOutboxConsumerManager>();

        Assert.Equal("rp-a", manager.Get("rp_a_group")?.ProviderKey);
        Assert.Equal("rp-b", manager.Get("rp_b_group")?.ProviderKey);
    }
}
