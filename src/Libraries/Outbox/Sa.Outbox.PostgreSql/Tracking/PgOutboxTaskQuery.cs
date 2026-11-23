using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// Filters for <see cref="IPgOutboxMessageTracker.SearchAsync"/>: a keyset page over the task table.
/// </summary>
/// <remarks>
/// Keyset pagination on <c>(msg_seq, task_id)</c> rather than <c>OFFSET</c>: the next page starts after
/// the last returned row instead of re-walking everything before it, so a deep page costs the same as
/// the first. Read the next cursor from <see cref="PgOutboxTaskEntry.Cursor"/>.
/// </remarks>
public sealed record PgOutboxTaskQuery
{
    /// <summary>Default page size.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The page size is capped so one call cannot pull an unbounded slice of the queue.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The tenant to search; required for partition pruning.</summary>
    public required int TenantId { get; init; }

    /// <summary>The message part to restrict to, when known.</summary>
    public string? Part { get; init; }

    /// <summary>The consumer group to restrict to, or <see langword="null"/> for every group.</summary>
    public string? ConsumerGroup { get; init; }

    /// <summary>
    /// The statuses to include, or <see langword="null"/> for all of them. An empty collection matches
    /// nothing — it cannot mean "everything".
    /// </summary>
    public IReadOnlyCollection<DeliveryStatusCode>? Statuses { get; init; }

    /// <summary>Start of the data window (inclusive). Defaults to seven days before <see cref="ToDate"/>.</summary>
    public DateTimeOffset? FromDate { get; init; }

    /// <summary>End of the data window (inclusive). Defaults to the current time.</summary>
    public DateTimeOffset? ToDate { get; init; }

    /// <summary>The position to continue after, or <see langword="null"/> to start from the beginning.</summary>
    public PgOutboxSearchCursor? After { get; init; }

    /// <summary>The page size, between 1 and <see cref="MaxLimit"/>. Defaults to <see cref="DefaultLimit"/>.</summary>
    public int Limit { get; init; } = DefaultLimit;
}
