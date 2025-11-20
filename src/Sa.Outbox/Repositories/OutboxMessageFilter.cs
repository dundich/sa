namespace Sa.Outbox;

/// <summary>
/// Represents a filter for querying Outbox messages based on specific criteria.
/// This record is used to define the parameters for filtering messages in the Outbox.
/// </summary>
public sealed record OutboxMessageFilter(
    /// <summary>
    /// Gets the transaction identifier associated with the Outbox message.
    /// </summary>
    string TransactId,

    /// <summary>
    /// Gets the type of the payload contained in the Outbox message.
    /// </summary>
    string PayloadType,

    /// <summary>
    /// Gets the identifier for the tenant associated with the Outbox message.
    /// </summary>
    int TenantId,

    /// <summary>
    /// Gets the part identifier for the Outbox message.
    /// </summary>
    string Part,

    /// <summary>
    /// Gets the starting date and time for filtering messages.
    /// Only messages created on or after this date will be included.
    /// </summary>
    DateTimeOffset FromDate,

    /// <summary>
    /// Gets the current date and time for filtering messages.
    /// Only messages created on or before this date will be included.
    /// </summary>
    DateTimeOffset NowDate
);