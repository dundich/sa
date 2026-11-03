using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Serialization;
using System.Text.Json;

namespace Sa.Outbox.PostgreSqlTests.Serialization;

/// <summary>
/// The PostgreSQL provider consumes the core serialization strategy: with no serializer of its own
/// it resolves the core JSON fallback, a provider-level <c>WithMessageSerializer</c> replaces it,
/// and a serializer registered under the old provider-specific interface still reaches the core
/// service.
/// </summary>
public class PgOutboxSerializerCompositionTests
{
    private sealed record Order(int Id);

    private sealed class CoreSerializer : IOutboxMessageSerializer
    {
        public static CoreSerializer Instance { get; } = new();

        public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream);

        public void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
    }

    private sealed class LegacySerializer : Sa.Outbox.PostgreSql.Serialization.IOutboxMessageSerializer
    {
        public static LegacySerializer Instance { get; } = new();

        public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream);

        public void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
    }

    [Fact]
    public void WithoutSerializer_UsesCoreJsonFallback()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSaOutboxUsingPostgreSql()
            .BuildServiceProvider();

        Assert.Same(
            JsonOutboxMessageSerializer.Instance,
            provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void ProviderWithMessageSerializer_ReplacesCoreFallback()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSaOutboxUsingPostgreSql(cfg => cfg.WithMessageSerializer(CoreSerializer.Instance))
            .BuildServiceProvider();

        Assert.Same(CoreSerializer.Instance, provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void LegacyInterfaceImplementation_IsAcceptedByProviderConfiguration()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddSaOutboxUsingPostgreSql(cfg => cfg.WithMessageSerializer(LegacySerializer.Instance))
            .BuildServiceProvider();

        Assert.Same(LegacySerializer.Instance, provider.GetRequiredService<IOutboxMessageSerializer>());
    }

    [Fact]
    public void LegacyRegistrationInDi_IsBridgedToCoreService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Sa.Outbox.PostgreSql.Serialization.IOutboxMessageSerializer>(
            LegacySerializer.Instance);

        using ServiceProvider provider = services
            .AddSaOutboxUsingPostgreSql()
            .BuildServiceProvider();

        Assert.Same(LegacySerializer.Instance, provider.GetRequiredService<IOutboxMessageSerializer>());
    }
}
