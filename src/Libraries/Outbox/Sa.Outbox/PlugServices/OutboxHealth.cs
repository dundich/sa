using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Runs every registered provider's optional health check and aggregates the reports.
/// </summary>
/// <remarks>
/// Checks run sequentially: there are a handful of providers and the endpoint is not on a hot path.
/// A provider without an <see cref="IOutboxProviderHealth"/> is reported as healthy, and a throwing
/// check is reported as unhealthy with the exception message — one broken provider must not fail
/// the whole aggregate with an exception.
/// </remarks>
internal sealed class OutboxHealth(
    IServiceProvider services,
    IEnumerable<OutboxProviderRegistration> registrations) : IOutboxHealth
{
    public async ValueTask<IReadOnlyList<OutboxProviderHealthReport>> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        string[] providerKeys =
        [
            .. registrations
                .Select(static r => r.ProviderKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        OutboxProviderHealthReport[] reports = new OutboxProviderHealthReport[providerKeys.Length];

        for (int i = 0; i < providerKeys.Length; i++)
        {
            string providerKey = providerKeys[i];
            IOutboxProviderHealth? health = services.GetKeyedService<IOutboxProviderHealth>(providerKey);

            if (health is null)
            {
                reports[i] = new OutboxProviderHealthReport(providerKey, Healthy: true);
                continue;
            }

            long started = Stopwatch.GetTimestamp();

            try
            {
                OutboxProviderHealthReport report = await health
                    .CheckAsync(cancellationToken)
                    .ConfigureAwait(false);

                reports[i] = report with
                {
                    ProviderKey = providerKey,
                    Latency = report.Latency == TimeSpan.Zero
                        ? Stopwatch.GetElapsedTime(started)
                        : report.Latency,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                reports[i] = new OutboxProviderHealthReport(
                    providerKey,
                    Healthy: false,
                    Reason: ex.Message,
                    Latency: Stopwatch.GetElapsedTime(started));
            }
        }

        return reports;
    }
}
