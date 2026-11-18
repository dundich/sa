using Microsoft.Extensions.Configuration;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// The PostgreSQL implementation of <see cref="IDeliveryGroupOptionsProvider"/>: applies the
/// provider-wide polling defaults (<see cref="PgPollingSettings"/>), then binds a consumer group's
/// <see cref="PgOutboxDeliveryOptions"/> from the <c>IConfiguration</c> section named by
/// <c>configurationKey</c>.
/// </summary>
/// <remarks>
/// Registered (replacing the base no-op) by <c>AddSaOutboxUsingPostgreSql</c>. The
/// <c>IConfiguration</c> is resolved once, at construction, as an optional service — a bare
/// <c>IServiceCollection</c> with no configuration (e.g. some test fixtures) makes
/// the provider a no-op instead of failing. When the <c>configurationKey</c> is empty or the
/// section is absent nothing is bound from configuration, so the group is shaped by
/// <see cref="OutboxDefaults"/>, the polling defaults and the user <c>configure</c> callback.
/// <para>
/// <see cref="PgPollingSettings"/> is optional too, and is applied <em>before</em> the section, which
/// gives the documented precedence: core defaults → polling defaults → the group's section → the
/// user's <c>AddDelivery</c> callback.
/// </para>
/// </remarks>
internal sealed class PgOutboxDeliveryOptionsProvider : IDeliveryGroupOptionsProvider
{
    private readonly IConfiguration? _configuration;
    private readonly PgPollingSettings? _polling;

    public PgOutboxDeliveryOptionsProvider(IConfiguration? configuration, PgPollingSettings? polling = null)
    {
        _configuration = configuration;
        _polling = polling;
    }

    public bool TryApply(string consumerGroupId, string? configurationKey, OutboxConsumerSettingsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        bool applied = _polling is { Enabled: true } && _polling.ApplyTo(builder);

        if (_configuration is null || string.IsNullOrWhiteSpace(configurationKey))
            return applied;

        PgOutboxDeliveryOptions? options = PgOutboxDeliveryOptions.FromConfiguration(_configuration, configurationKey);
        if (options is null)
            return applied;

        return options.ApplyTo(builder) || applied;
    }
}
