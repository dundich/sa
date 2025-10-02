using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

internal sealed class DeliveryProcessor(IDeliveryRelay relayService) : IDeliveryProcessor
{
    public async Task<long> ProcessMessages<TMessage>(OutboxDeliverySettings settings, CancellationToken cancellationToken)
        where TMessage : IOutboxPayloadMessage
    {
        long count = 0;
        bool runAgain;
        do
        {
            int sentCount = await relayService.StartDelivery<TMessage>(settings, cancellationToken);
            runAgain = sentCount > 0;
            count += sentCount;
        }
        while (runAgain && !cancellationToken.IsCancellationRequested);

        return count;
    }
}
