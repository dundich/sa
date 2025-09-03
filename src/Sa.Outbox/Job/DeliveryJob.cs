using Sa.Outbox.Delivery;
using Sa.Outbox.Publication;
using Sa.Outbox.Support;
using Sa.Schedule;

namespace Sa.Outbox.Job;


public interface IDeliveryJob : IJob;


internal class DeliveryJob<TMessage>(IDeliveryProcessor processor) : IDeliveryJob
    where TMessage : IOutboxPayloadMessage
{

    static DeliveryJob()
    {
        OutboxMessageTypeHelper.GetOutboxMessageTypeInfo<TMessage>();
    }

    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        OutboxDeliverySettings settings = context.Settings.Properties.Tag as OutboxDeliverySettings
            ?? throw new NotImplementedException("tag");

        await processor.ProcessMessages<TMessage>(settings, cancellationToken);
    }
}
