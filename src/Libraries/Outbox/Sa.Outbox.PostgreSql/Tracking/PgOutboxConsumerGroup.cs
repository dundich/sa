namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// A consumer group the PostgreSQL storage knows about, with the position of its consumption cursor.
/// </summary>
/// <remarks>
/// The offset table is the storage-side registry: a row is created on the group's first poll, so a
/// group that is registered in DI but has never started is absent. Comparing <see cref="OffsetSeq"/>
/// with <see cref="PgOutboxMessageTrace.MsgSeq"/> tells whether that group has already passed a
/// message — while <see cref="PgOutboxMessageTrace.GetMissingGroups"/> tells whether it has a task for
/// it.
/// </remarks>
public sealed record PgOutboxConsumerGroup
{
    /// <summary>The consumer group id.</summary>
    public required string ConsumerGroup { get; init; }

    /// <summary>The tenant this cursor belongs to.</summary>
    public required int TenantId { get; init; }

    /// <summary>
    /// The last <c>msg_seq</c> the group has loaded. Messages at or below it were already handed to
    /// the task table (or skipped by a configured floor).
    /// </summary>
    public required long OffsetSeq { get; init; }

    /// <summary>When the cursor was last advanced, when the storage recorded it.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
