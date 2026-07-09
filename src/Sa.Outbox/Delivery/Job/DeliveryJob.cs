using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;


public interface IDeliveryJob : IJob;


internal sealed class DeliveryJob<TMessage>(
    IDeliveryProcessor processor,
    IOutboxConsumerManager settingsManager) : IDeliveryJob
{
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        var settings = settingsManager.Get(context.JobName)
            ?? throw new InvalidOperationException($"No OutboxConsumerSettings for consumer group '{context.JobName}'.");

        await processor.ProcessMessages<TMessage>(settings, cancellationToken).ConfigureAwait(false);
    }
}
