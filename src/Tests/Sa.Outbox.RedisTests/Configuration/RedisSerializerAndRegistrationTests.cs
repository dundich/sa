using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.Redis;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Services.Plug;
using Sa.Outbox.Serialization;

namespace Sa.Outbox.RedisTests.Configuration;

/// <summary>
/// Provider-level registration rules: the serializer is keyed by the provider key (a second provider
/// never overwrites it, and one without an explicit serializer follows the container-level one), the
/// layout guard has one marker per key, and an <c>Options</c> action can pin an exact prefix.
/// </summary>
public class RedisSerializerAndRegistrationTests
{
    /// <summary>Delegates to JSON so the storage behaviour is unchanged; the type is the marker.</summary>
    private sealed class MarkerSerializer(IOutboxMessageSerializer inner) : IOutboxMessageSerializer
    {
        public T? Deserialize<T>(Stream stream) => inner.Deserialize<T>(stream);

        public void Serialize<T>(Stream stream, T value) => inner.Serialize(stream, value);
    }

    [Fact]
    public void Serializer_IsRegisteredPerProviderKey()
    {
        var explicitSerializer = new MarkerSerializer(JsonOutboxMessageSerializer.Instance);
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis(
            cfg => cfg.WithConnectionString("localhost:6379").WithMessageSerializer(explicitSerializer),
            scopeKey: "redis-a");

        services.AddSaOutboxUsingRedis(
            cfg => cfg.WithConnectionString("localhost:6379"),
            scopeKey: "redis-b");

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(explicitSerializer, provider.GetRequiredKeyedService<IOutboxMessageSerializer>("redis-a"));

        // A provider without its own serializer follows the container-level (core JSON) one.
        Assert.Same(
            JsonOutboxMessageSerializer.Instance,
            provider.GetRequiredKeyedService<IOutboxMessageSerializer>("redis-b"));
    }

    [Fact]
    public void Serializer_OnDefaultKey_StaysAvailableUnkeyed()
    {
        var serializer = new MarkerSerializer(JsonOutboxMessageSerializer.Instance);
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis(cfg => cfg
            .WithConnectionString("localhost:6379")
            .WithMessageSerializer(serializer));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(
            serializer,
            provider.GetRequiredKeyedService<IOutboxMessageSerializer>(OutboxScope.DefaultKey));

        // Backward compatibility: the classic single-provider surface resolves it without a key.
        Assert.Same(serializer, provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void SameKeyRegisteredTwice_IsIdempotent()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis(cfg => cfg.WithConnectionString("localhost:6379"), scopeKey: "dup");
        services.AddSaOutboxUsingRedis(cfg => cfg.WithConnectionString("localhost:6379"), scopeKey: "dup");

        // One layout marker: the guard must not compare the provider with itself.
        Assert.Single(services, d => d.ServiceType == typeof(RedisProviderLayout));
    }

    [Fact]
    public void SameKeyRegisteredTwice_AppliesTheSettingsCallbacksOnce()
    {
        var services = new ServiceCollection();
        int calls = 0;

        services.AddSaOutboxUsingRedis(Settings, scopeKey: "dup-settings");
        services.AddSaOutboxUsingRedis(Settings, scopeKey: "dup-settings");

        using ServiceProvider provider = services.BuildServiceProvider();

        RedisOutboxSettings settings = provider.GetRequiredKeyedService<RedisOutboxSettings>("dup-settings");

        // A repeated registration replaces the callbacks it recorded earlier; it must not make the
        // same callback run once per registration.
        Assert.Equal("sa:outbox:dup", settings.KeySettings.Prefix);
        Assert.Equal(1, calls);

        void Settings(IRedisOutboxConfiguration cfg) => cfg
            .WithConnectionString("localhost:6379")
            .WithOutboxSettings((_, s) =>
            {
                calls++;
                s.KeySettings.Prefix = "sa:outbox:dup";
            });
    }

    [Fact]
    public void OptionsAction_PinsAnExactPrefixOnANonDefaultKey()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis(
            cfg => cfg
                .WithConnectionString("localhost:6379")
                // The provider appends ":{key}" after WithOutboxSettings/WithConfiguration; an
                // Options action runs last and can therefore pin the exact prefix.
                .Options(options => options.Configure(s => s.KeySettings.Prefix = "sa:outbox")),
            scopeKey: "billing");

        using ServiceProvider provider = services.BuildServiceProvider();

        RedisOutboxSettings settings = provider.GetRequiredKeyedService<RedisOutboxSettings>("billing");
        Assert.Equal("sa:outbox", settings.KeySettings.Prefix);
    }
}
