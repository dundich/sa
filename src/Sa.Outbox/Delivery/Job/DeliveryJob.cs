using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;


public interface IDeliveryJob : IJob;


internal sealed class DeliveryJob<TMessage>(
    IDeliveryProcessor processor,
    IOutboxConsumerManager settingsManager) : IDeliveryJob
{
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        var settings = settingsManager.Get(context.JobName);

        if (settings is null)
        {
            // Auto-bootstrap: first execution hasn't been registered yet.
            settings = context.Settings.Properties.GetConsumerGroupSettings()
                ?? throw new InvalidOperationException(
                    $"No OutboxConsumerSettings for consumer group '{context.JobName}'.");

            settingsManager.Register(context.JobName, settings);
        }

        await processor.ProcessMessages<TMessage>(settings, cancellationToken);
    }
}
