namespace Sa.Outbox.Support;

/// <summary>
/// Represents an entity or message that is logically associated with a specific partition ("part").
/// This allows routing messages to dedicated outbox tables or partitions based on type,
/// enabling sharding, tenant isolation, or workload separation in distributed systems.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="PartName"/> property defines the logical partition name (e.g., "payments", "notifications", "eu_region")
/// and is typically used to route messages to specific database tables like <c>outbox_{PartName}</c>.
/// </para>
/// <para>
/// This interface uses <c>static abstract</c> members (C# 11+), so implementation must provide a concrete value at the type level.
/// It does not depend on instance data, making it efficient for generic processing without instantiating objects.
/// </para>
/// <para>
/// Example scenarios:
/// <list type="bullet">
///   <item>Partitioning outbox by message type or domain.</item>
///   <item>Isolating tenants or regions in event queues.</item>
///   <item>Avoiding lock contention on a single outbox table.</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
public interface IOutboxHasPart
{
    /// <summary>
    /// Gets the logical identifier of the partition associated with this type.
    /// Used for routing messages to specific tables, shards, or storage groups (e.g., <c>outbox_orders</c>, <c>outbox_eu</c>).
    /// </summary>
    /// <value>
    /// A non-null string representing the partition name. Must be constant per type or derived from compile-time logic.
    /// Should avoid special characters; use lowercase letters, digits, and underscores.
    /// </value>
    /// <example>"orders", "notifications", "us_west"</example>
    static abstract string PartName { get; }
}
