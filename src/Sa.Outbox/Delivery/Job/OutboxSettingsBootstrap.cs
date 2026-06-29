using Microsoft.Extensions.Hosting;

namespace Sa.Outbox.Delivery.Job;

/// <summary>
/// Bootstrap service that registers all consumer group initial settings into
/// <see cref="IOutboxSettingsManager"/> after the DI container is fully built.
/// Runs once at application startup, before any scheduled jobs execute.
/// </summary>
internal sealed class OutboxSettingsBootstrap(
    IDeliverySnapshot snapshot,
    IOutboxSettingsManager settingsManager) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var settings in snapshot.ConsumerSettings)
        {
            // Register the canonical snapshot derived from bootstrap settings.
            var canonical = settings.ToCanonical();
            settingsManager.Register(settings.ConsumerGroupId, builder =>
                builder.BuildCopy(canonical));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
