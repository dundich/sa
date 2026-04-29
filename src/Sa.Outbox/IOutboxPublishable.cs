namespace Sa.Outbox;

public interface IOutboxPublishable
{
    string GetPayloadId();

    int GetTenantId();
    /// <summary>
    /// Gets the logical identifier of the partition associated with this type.
    /// Used for routing messages to specific tables, shards, or storage groups (e.g., <c>outbox_orders</c>, <c>outbox_eu</c>).
    /// </summary>
    /// <example>"orders", "notifications", "us_west"</example>
    static abstract string PartName { get; }
}
