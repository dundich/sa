using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Validates the consumer-group scope keys and forces the keyed
/// <see cref="IOutboxProviderRegistry"/> of every registered provider to be constructed at host
/// start, which validates each installation before any delivery job can run, and announces the
/// providers once.
/// </summary>
/// <remarks>
/// The registry constructor does the checks; this service only makes them happen at a known
/// moment. <c>AddSaOutbox</c> and every <c>AddOutboxProvider</c> call register it
/// (<c>TryAddEnumerable</c> deduplicates by implementation type), so the scope-key check also runs
/// when no provider package was called at all. The delivery job settings build re-resolves the
/// registry of its own key as a second line of defence for the opposite registration order.
/// </remarks>
internal sealed partial class OutboxProviderValidationHostedService(
    IEnumerable<OutboxProviderRegistration> registrations,
    IServiceProvider services,
    ILogger<OutboxProviderValidationHostedService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<OutboxProviderValidationHostedService>.Instance;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        HashSet<string> providerKeys = new(
            registrations.Select(static r => r.ProviderKey),
            StringComparer.Ordinal);

        // Scope-key consistency: every consumer group names the provider key its storage lives
        // under. A group whose key has no provider means AddSaOutbox and the provider package were
        // called with different scope keys — or the provider package was never called at all — and
        // the group's delivery would resolve no storage.
        ILookup<string, DeliveryGroupSettingsRegistration> groupsByScope = services
            .GetServices<DeliveryGroupSettingsRegistration>()
            .ToLookup(static registration => registration.ProviderKey, StringComparer.Ordinal);

        foreach (IGrouping<string, DeliveryGroupSettingsRegistration> groups in
            groupsByScope.OrderBy(static scope => scope.Key, StringComparer.Ordinal))
        {
            if (providerKeys.Contains(groups.Key)) continue;

            // Name the groups, not just the scope: one scope may carry several of them, and the
            // operator has to know which registrations were mis-scoped.
            string groupIds = string.Join(
                "', '",
                groups.Select(static registration => registration.ConsumerGroupId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal));

            throw new InvalidOperationException(
                $"Outbox consumer group(s) '{groupIds}' are registered under the scope " +
                $"'{groups.Key}', but no storage provider is registered for that key (registered " +
                $"providers: {string.Join(", ", providerKeys.Order(StringComparer.Ordinal))}). " +
                "Pass the same scopeKey to AddSaOutbox and the provider package, or register the " +
                "provider with that key.");
        }

        foreach (string providerKey in providerKeys.Order(StringComparer.Ordinal))
        {
            IOutboxProviderRegistry registry =
                services.GetRequiredKeyedService<IOutboxProviderRegistry>(providerKey);

            LogProvider(
                _logger,
                providerKey,
                registry.Descriptor.Name,
                registry.Descriptor.Version ?? "unknown",
                registry.Descriptor.Capabilities.ToString());
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Outbox storage provider '{ProviderKey}': {Provider} {Version} ({Capabilities})")]
    static partial void LogProvider(ILogger logger, string providerKey, string provider, string version, string capabilities);
}
