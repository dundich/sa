namespace Sa.Outbox.Delivery;

internal sealed class DeliveryBatcher : IDeliveryBatcher
{
    public ValueTask<int> CalculateBatchSize(int maxBatchSize, OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(maxBatchSize);
    }
}
