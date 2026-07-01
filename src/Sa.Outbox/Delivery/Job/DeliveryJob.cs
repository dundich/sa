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
            // Use TryRegister to avoid race conditions when multiple application
            // instances start simultaneously — only one will succeed, others skip.
            var bootstrapped = context.Settings.Properties.GetConsumerGroupSettings()
                ?? throw new InvalidOperationException(
                    $"No OutboxConsumerSettings for consumer group '{context.JobName}'.");

            bool registered = settingsManager.TryRegister(context.JobName, bootstrapped);
            if (!registered)
            {
                // Another instance registered us concurrently — read the canonical snapshot.
                settings = settingsManager.Get(context.JobName)!;
            }
            else
            {
                settings = bootstrapped;
            }
        }

        await processor.ProcessMessages<TMessage>(settings, cancellationToken).ConfigureAwait(false);
    }
}
