using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// How many tasks of a consumer group sit in one status, for dashboards and health checks.
/// </summary>
public sealed record PgOutboxStatusCount
{
    /// <summary>The consumer group the count belongs to.</summary>
    public required string ConsumerGroup { get; init; }

    /// <summary>The status the counted tasks are in.</summary>
    public required DeliveryStatusCode StatusCode { get; init; }

    /// <summary>The number of tasks in <see cref="StatusCode"/>.</summary>
    public required long Count { get; init; }

    /// <summary>
    /// How many of <see cref="Count"/> are <c>Processing</c> with an expired lease — the crashed-worker
    /// count. Zero for every other status.
    /// </summary>
    public required long Stuck { get; init; }
}
