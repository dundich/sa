using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql;
using Sa.Outbox;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Services.Plug;
using Sa.Outbox.Serialization;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Provider-level registration rules: the serializer is keyed by the provider key (a second provider
/// never overwrites it, and one without an explicit serializer follows the container-level one), and
/// registering the same key twice is idempotent.
/// </summary>
public class PgSerializerAndRegistrationTests
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

        services.AddSaOutboxUsingPostgreSql(
            cfg => cfg.WithDataSource().WithMessageSerializer(explicitSerializer),
            scopeKey: "pg-a");

        services.AddSaOutboxUsingPostgreSql(
            cfg => cfg.WithDataSource(),
            scopeKey: "pg-b");

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(explicitSerializer, provider.GetRequiredKeyedService<IOutboxMessageSerializer>("pg-a"));

        // A provider without its own serializer follows the container-level (core JSON) one.
        Assert.Same(
            JsonOutboxMessageSerializer.Instance,
            provider.GetRequiredKeyedService<IOutboxMessageSerializer>("pg-b"));
    }

    [Fact]
    public void Serializer_OnDefaultKey_StaysAvailableUnkeyed()
    {
        var serializer = new MarkerSerializer(JsonOutboxMessageSerializer.Instance);
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(cfg => cfg.WithDataSource().WithMessageSerializer(serializer));

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

        services.AddSaOutboxUsingPostgreSql(Configure, scopeKey: "dup");
        services.AddSaOutboxUsingPostgreSql(Configure, scopeKey: "dup");

        // One layout marker (the guard must not compare the provider with itself) and one data source.
        Assert.Single(services, d => d.ServiceType == typeof(PgProviderLayout));
        Assert.Single(services, d => d.ServiceType == typeof(IPgDataSource) && d.IsKeyedService);

        static void Configure(IPgOutboxConfiguration pg)
            => pg.WithDataSource(ds => ds.Options(options => options.Configure(
                o => o.ConnectionString = "Host=localhost;Database=outbox")));
    }

    [Fact]
    public void SameKeyRegisteredTwice_RecordsTheSettingsCallbacksOnce()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(Settings, scopeKey: "dup-settings");
        services.AddSaOutboxUsingPostgreSql(Settings, scopeKey: "dup-settings");

        // The settings factory applies every action of the key, so a repeated registration replaces
        // the callbacks it recorded earlier instead of making them run once per registration.
        Assert.Single(
            services,
            d => d.ServiceType == typeof(Action<IServiceProvider, PgOutboxSettings>)
                && Equals(d.ServiceKey, "dup-settings"));

        static void Settings(IPgOutboxConfiguration pg)
            => pg.WithOutboxSettings((_, settings) => settings.TableSettings.DatabaseSchemaName = "dup");
    }
}
