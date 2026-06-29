using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;


public interface IDeliveryJob : IJob;


internal sealed class DeliveryJob<TMessage>(IDeliveryProcessor processor) : IDeliveryJob
{
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        OutboxConsumerSettings settings = context.Settings.Properties.GetConsumerGroupSettings()
            ?? throw new InvalidOperationException("Missing OutboxConsumerSettings tag on job.");

        await processor.ProcessMessages<TMessage>(settings, cancellationToken);
    }
}
