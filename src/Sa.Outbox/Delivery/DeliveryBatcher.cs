namespace Sa.Outbox.Delivery;

/// <summary>
/// Default batch size calculator — a passthrough stub.
///
/// This implementation ignores the filter and always returns the requested
/// <paramref name="maxBatchSize"/> clamped to [0, maxBatchSize].
///
/// It exists solely as a built-in default so the DI container always has an
/// <see cref="IDeliveryBatcher"/> registered. For production workloads where
/// adaptive batching based on system load or queue depth is needed, register
/// your own implementation via <see cref="IOutboxBuilder.AddDeliveryBatching{TImplementation}"/>
/// or <see cref="DeliveryBuilder.AddDeliveryBatching{TImplementation}"/>.
/// </summary>
internal sealed class DeliveryBatcher : IDeliveryBatcher
{
    public ValueTask<int> CalculateBatchSize(
        int maxBatchSize,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(maxBatchSize);
    }
}
