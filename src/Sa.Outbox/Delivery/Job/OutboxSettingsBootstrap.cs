namespace Sa.Outbox.Delivery.Job;

/// <summary>
/// Bootstrap service that registers all consumer group initial settings into
/// <see cref="IOutboxConsumerManager"/> after the DI container is fully built.
/// Runs once at application startup, before any scheduled jobs execute.
/// </summary>
internal sealed class OutboxSettingsBootstrap(
    IDeliverySnapshot snapshot,
    IOutboxConsumerManager settingsManager)
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var settings in snapshot.ConsumerSettings)
        {
            // Register the settings directly — no conversion needed anymore.
            settingsManager.Register(settings.ConsumerGroupId, settings);
        }

        return Task.CompletedTask;
    }
}
