namespace Sa.Outbox.Delivery;

/// <summary>
/// Provides functionality to dynamically calculate batch sizes based on current system load or other metrics.
/// </summary>
public interface IDeliveryBatcher
{
    /// <summary>
    /// Calculates the optimal batch size given a maximum allowed size and a specific tenant ID.
    /// </summary>
    ValueTask<int> CalculateBatchSize(int maxBatchSize, OutboxMessageFilter filter, CancellationToken cancellationToken);
}
