namespace Sa.Outbox;


/// <summary>
/// Represents a message in the Outbox with its associated payload and part information.
/// </summary>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
public record struct OutboxMessage<TMessage>(
    /// <summary>
    /// Gets the unique identifier for the payload.
    /// </summary>
    string PayloadId,

    /// <summary>
    /// Gets the actual message payload.
    /// </summary>
    TMessage Payload,

    /// <summary>
    /// Gets information about the part of the Outbox message.
    /// </summary>
    OutboxPartInfo PartInfo
);

/// <summary>
/// Represents a delivery message in the Outbox with its associated payload, part information, and delivery details.
/// </summary>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
public record struct OutboxDeliveryMessage<TMessage>(
    /// <summary>
    /// Gets the unique identifier for the Outbox delivery.
    /// </summary>
    string OutboxId,

    /// <summary>
    /// Gets the unique identifier for the payload.
    /// </summary>
    string PayloadId,

    /// <summary>
    /// Gets the actual message payload.
    /// </summary>
    TMessage Payload,

    /// <summary>
    /// Gets information about the part of the Outbox message.
    /// </summary>
    OutboxPartInfo PartInfo,

    /// <summary>
    /// Gets information about the delivery of the Outbox message.
    /// </summary>
    OutboxDeliveryInfo DeliveryInfo
);

/// <summary>
/// Represents information about a part of the Outbox message.
/// </summary>
public record struct OutboxPartInfo(
    /// <summary>
    /// Gets the identifier for the tenant associated with the message.
    /// </summary>
    int TenantId,

    /// <summary>
    /// Gets the part identifier for the Outbox message.
    /// </summary>
    string Part,

    /// <summary>
    /// Gets the date and time when the part was created.
    /// </summary>
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
public record struct OutboxDeliveryInfo(
    string? DeliveryId,
    int Attempt,
    string LastErrorId,
    DeliveryStatus Status,
    DateTimeOffset CreatedAt
);

/// <summary>
/// Represents the status of a delivery attempt.
/// </summary>
/// <param name="Code">The status code representing the result of the delivery.</param>
/// <param name="Message">A message providing additional context about the delivery status.</param>
/// <param name="CreatedAt">The date and time when the status was created.</param>
public record struct DeliveryStatus(
    int Code,
    string Message,
    DateTimeOffset CreatedAt
);