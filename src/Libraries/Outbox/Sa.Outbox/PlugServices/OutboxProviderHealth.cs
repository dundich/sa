namespace Sa.Outbox.PlugServices;

/// <summary>
/// The outcome of one provider's health check.
/// </summary>
/// <param name="ProviderKey">The provider key the report belongs to.</param>
/// <param name="Healthy">Whether the provider is usable right now.</param>
/// <param name="Reason">A human-readable reason; <see langword="null"/> when healthy.</param>
/// <param name="Latency">How long the check took.</param>
public readonly record struct OutboxProviderHealthReport(
    string ProviderKey,
    bool Healthy,
    string? Reason = null,
    TimeSpan Latency = default);

/// <summary>
/// An optional, provider-owned health check. Registered keyed by provider key; a provider that does
/// not implement it is reported as healthy with no reason (the aggregate cannot invent a check).
/// </summary>
/// <remarks>
/// The check should be cheap: it is called by the application's readiness endpoint, not by the
/// delivery loop. The PostgreSQL provider runs <c>SELECT 1</c>, the Redis provider runs
/// <c>PING</c>.
/// </remarks>
public interface IOutboxProviderHealth
{
    /// <summary>Runs the check.</summary>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The provider's report (its <see cref="OutboxProviderHealthReport.ProviderKey"/> is stamped by the aggregate).</returns>
    ValueTask<OutboxProviderHealthReport> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregated health over every provider registered in the container.
/// </summary>
/// <remarks>
/// The contract is deliberately transport-agnostic (no <c>Microsoft.Extensions.Diagnostics.HealthChecks</c>
/// dependency in the core): map it to an ASP.NET health check in one line, or to your own endpoint.
/// A provider without an <see cref="IOutboxProviderHealth"/> is reported as healthy with a
/// <see langword="null"/> reason, so adding a provider does not silently turn readiness red.
/// </remarks>
public interface IOutboxHealth
{
    /// <summary>Runs every provider's check and returns one report per provider key.</summary>
    /// <param name="cancellationToken">A token to cancel the checks.</param>
    /// <returns>The reports, ordered by provider key.</returns>
    ValueTask<IReadOnlyList<OutboxProviderHealthReport>> CheckAsync(CancellationToken cancellationToken = default);
}
