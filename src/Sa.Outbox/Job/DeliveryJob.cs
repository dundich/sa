using Sa.Outbox.Delivery;
using Sa.Schedule;

namespace Sa.Outbox.Job;


public interface IDeliveryJob: IJob;


internal class DeliveryJob<TMessage>(IDeliveryProcessor processor) : IDeliveryJob
{
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        OutboxDeliverySettings settings = context.Settings.Properties.Tag as OutboxDeliverySettings
            ?? throw new NotImplementedException("tag");

        await processor.ProcessMessages<TMessage>(settings, cancellationToken);
    }
}
