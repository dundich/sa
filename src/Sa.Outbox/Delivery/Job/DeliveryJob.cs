using Sa.Outbox.Publication;
using Sa.Outbox.Support;
using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;


public interface IDeliveryJob : IJob;


internal sealed class DeliveryJob<TMessage>(IDeliveryProcessor processor) : IDeliveryJob
    where TMessage : IOutboxPayloadMessage
{

    static DeliveryJob()
    {
        OutboxMessageTypeHelper.GetOutboxMessageTypeInfo<TMessage>();
    }

    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        ConsumerGroupSettings settings = context.Settings.Properties.Tag as ConsumerGroupSettings
            ?? throw new NotImplementedException("tag");

        await processor.ProcessMessages<TMessage>(settings, cancellationToken);
    }
}
