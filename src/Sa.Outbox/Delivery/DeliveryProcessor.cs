using Sa.Outbox.Support;

namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes outbox messages in batches until all pending messages are delivered or cancellation is requested.
/// Implements a continuous polling pattern to ensure reliable message delivery.
/// </summary>
internal sealed class DeliveryProcessor(IDeliveryRelay relayService) : IDeliveryProcessor
{
    public async Task<long> ProcessMessages<TMessage>(ConsumeSettings settings, CancellationToken cancellationToken)
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