namespace Sa.Outbox;

/// <summary>
/// Represents information about a part of the Outbox message.
/// </summary>
/// <param name="TenantId">Gets the identifier for the tenant associated with the message.</param>
/// <param name="Part">Gets the part identifier for the Outbox message.</param>
/// <param name="CreatedAt">Gets the date and time when the part was created.</param>
public readonly record struct OutboxPartInfo(
    int TenantId,
    string Part,
    DateTimeOffset CreatedAt
);
