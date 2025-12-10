namespace Sa.Outbox;

/// <summary>
/// Represents a filter for querying Outbox messages based on specific criteria.
/// This record is used to define the parameters for filtering messages in the Outbox.
/// </summary>
/// <param name="TransactId">Gets the transaction identifier associated with the Outbox message.</param>
/// <param name="ConsumerGroupId"></param>
/// <param name="PayloadType">Gets the type of the payload contained in the Outbox message.</param>
/// <param name="TenantId">Gets the identifier for the tenant associated with the Outbox message.</param>
/// <param name="Part">Gets the part identifier for the Outbox message.</param>
/// <param name="FromDate">Gets the starting date and time for filtering messages.Only messages created on or after this date will be included.</param>
/// <param name="ToDate">Gets the current date and time for filtering messages.</param>
public sealed record OutboxMessageFilter(
    string TransactId,
    string ConsumerGroupId,
    string PayloadType,
    int TenantId,
    string Part,
    DateTimeOffset FromDate,
    DateTimeOffset ToDate
);
