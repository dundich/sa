namespace Sa.Outbox;


/// <summary>
/// Represents a message in the Outbox with its associated payload and part information.
/// </summary>
/// <param name="PayloadId">Gets the unique identifier for the payload.</param>
/// <param name="Payload">Gets the actual message payload.</param>
/// <param name="PartInfo">Gets information about the part of the Outbox message.</param>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
public sealed record OutboxMessage<TMessage>(
    string PayloadId,
    TMessage Payload,
    OutboxPartInfo PartInfo
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
