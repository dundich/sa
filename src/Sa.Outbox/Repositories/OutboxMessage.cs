namespace Sa.Outbox;


/// <summary>
/// Represents a message in the Outbox with its associated payload and part information.
/// </summary>
/// <param name="PartInfo">Gets the unique identifier for the payload.</param>
/// <param name="Payload">Gets the actual message payload.</param>
/// <param name="PayloadId">Gets information about the part of the Outbox message.</param>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
public sealed record OutboxMessage<TMessage>(
    string PayloadId,
    TMessage Payload,
    OutboxPartInfo PartInfo
);

/// <summary>
/// Represents a delivery message in the Outbox with its associated payload, part information, and delivery details.
/// </summary>
/// <param name="DeliveryInfo">Gets the unique identifier for the Outbox delivery.</param>
/// <param name="Message">Message in the Outbox.</param>
/// <param name="OutboxId">Gets information about the delivery of the Outbox message.</param>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
public sealed record OutboxDeliveryMessage<TMessage>(
    string OutboxId,
    OutboxMessage<TMessage> Message,
    OutboxDeliveryInfo DeliveryInfo
);

/// <summary>
/// Represents information about a part of the Outbox message.
/// </summary>
/// <param name="TenantId">Gets the identifier for the tenant associated with the message..</param>
/// <param name="Part">Gets the part identifier for the Outbox message.</param>
/// <param name="CreatedAt">Gets the date and time when the part was created.</param>
public sealed record OutboxPartInfo(
    int TenantId,
    string Part,
    DateTimeOffset CreatedAt
);

/// <summary>
/// Represents information about the delivery of an Outbox message.
/// </summary>
/// <param name="DeliveryId">The unique identifier for the delivery.</param>
/// <param name="Attempt">The number of delivery attempts made.</param>
/// <param name="LastErrorId">The identifier of the last error encountered during delivery.</param>
/// <param name="Status">The current status of the delivery.</param>
/// <param name="CreatedAt">The date and time when the delivery was created.</param>
public sealed record OutboxDeliveryInfo(
    string? DeliveryId,
    int Attempt,
    string LastErrorId,
    DeliveryStatus Status
);

/// <summary>
/// Represents the status of a delivery attempt.
/// </summary>
/// <param name="Code">The status code representing the result of the delivery.</param>
/// <param name="Message">A message providing additional context about the delivery status.</param>
/// <param name="CreatedAt">The date and time when the status was created.</param>
public readonly record struct DeliveryStatus(
    int Code,
    string Message,
    DateTimeOffset CreatedAt
);
