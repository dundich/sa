using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Forces the <see cref="IOutboxProviderRegistry"/> to be constructed at host start, which validates
/// the provider installation before any delivery job can run, and announces the provider once.
/// </summary>
/// <remarks>
/// The registry's constructor does the checks; this service only makes them happen at a known
/// moment. The provider packages register it before <c>AddSaOutbox</c>, and the delivery job
/// settings build re-resolves the registry as a second line of defence for the opposite
/// registration order.
/// </remarks>
internal sealed partial class OutboxProviderValidationHostedService(
    IOutboxProviderRegistry registry,
    ILogger<OutboxProviderValidationHostedService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<OutboxProviderValidationHostedService>.Instance;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        OutboxProviderDescriptor descriptor = registry.Descriptor;

        LogProvider(
            _logger,
            descriptor.Name,
            descriptor.Version ?? "unknown",
            descriptor.Capabilities.ToString());

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Outbox storage provider: {Provider} {Version} ({Capabilities})")]
    static partial void LogProvider(ILogger logger, string provider, string version, string capabilities);
}
