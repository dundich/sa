namespace Sa.Outbox.Delivery;

/// <summary>
/// Represents information about the delivery of an Outbox message.
/// </summary>
/// <param name="TaskId">The unique identifier of the processing task associated with the Outbox message.
/// Each message processing creates a separate task that can be retried in case of delivery failures.</param>
/// <param name="DeliveryId">The identifier of the latest delivery attempt.
/// Multiple delivery attempts can be made for the same task, each with its own DeliveryId.
/// 0 if no delivery attempts have been made yet.</param>
/// <param name="Attempt">The number of delivery attempts made for this task.
/// Incremented with each retry attempt.</param>
/// <param name="LastErrorId">The identifier of the last error encountered during delivery.
/// Used for error tracking and monitoring.</param>
/// <param name="Status">The current status of the delivery.</param>
/// <param name="PartInfo">Information about the partition/segment of the Outbox.</param>
public sealed record OutboxTaskDeliveryInfo(
    long TaskId,
    long DeliveryId,
    int Attempt,
    long LastErrorId,
    DeliveryStatus Status,
    OutboxPartInfo PartInfo
);
