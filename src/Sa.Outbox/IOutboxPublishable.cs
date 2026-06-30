namespace Sa.Outbox;

/// <summary>
/// Marks a message type as publishable through the Outbox pattern.
/// Implementors supply a unique payload identifier, the owning tenant ID, and the logical partition name used for routing.
/// </summary>
public interface IOutboxPublishable
{
    /// <summary>
    /// Gets or produces a unique identifier for the message payload.
    /// Used by the Outbox infrastructure to track delivery attempts and prevent duplicates.
    /// </summary>
    string GetPayloadId();

    /// <summary>
    /// Gets the tenant identifier that owns this message type.
    /// </summary>
    int GetTenantId();
    /// <summary>
    /// Gets the logical identifier of the partition associated with this type.
    /// Used for routing messages to specific tables, shards, or storage groups (e.g., <c>outbox_orders</c>, <c>outbox_eu</c>).
    /// </summary>
    /// <example>"orders", "notifications", "us_west"</example>
    static abstract string PartName { get; }
}
