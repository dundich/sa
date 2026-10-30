using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// The PostgreSQL implementation of <see cref="IDeliveryGroupOptionsProvider"/>: binds a
/// consumer group's <see cref="PgOutboxDeliveryOptions"/> from the <c>IConfiguration</c>
/// section named by <paramref name="configurationKey"/> and applies the non-null values to
/// the settings builder.
/// </summary>
/// <remarks>
/// Registered (replacing the base no-op) by <c>AddSaOutboxUsingPostgreSql</c>. The
/// <c>IConfiguration</c> is resolved once, at construction, as an optional service — a bare
/// <see cref="IServiceCollection"/> with no configuration (e.g. some test fixtures) makes
/// the provider a no-op instead of failing. A per-call no-op is also returned when the
/// <c>configurationKey</c> is empty or the section is absent, so a group is then shaped
/// purely by <see cref="OutboxDefaults"/> and the user <c>configure</c> callback.
/// </remarks>
internal sealed class PgOutboxDeliveryOptionsProvider : IDeliveryGroupOptionsProvider
{
    private readonly IConfiguration? _configuration;

    public PgOutboxDeliveryOptionsProvider(IConfiguration? configuration)
    {
        _configuration = configuration;
    }

    public bool TryApply(string consumerGroupId, string? configurationKey, OutboxConsumerSettingsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (_configuration is null || string.IsNullOrWhiteSpace(configurationKey))
            return false;

        PgOutboxDeliveryOptions? options = PgOutboxDeliveryOptions.FromConfiguration(_configuration, configurationKey);
        if (options is null)
            return false;

        return options.ApplyTo(builder);
    }
}
