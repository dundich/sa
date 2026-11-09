namespace Sa.Outbox.Delivery;

/// <summary>
/// Default batch size calculator — a passthrough stub.
///
/// This implementation ignores the filter and returns <paramref name="maxBatchSize"/> unchanged;
/// the <c>[0, maxBatchSize]</c> clamp is applied by the caller (<c>DeliveryTenant</c>), so a
/// custom implementation may also return an out-of-range value and have it clamped.
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
