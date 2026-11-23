namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// One delivery task of one consumer group, as returned by a search.
/// </summary>
/// <remarks>
/// A task is the unit a queue dashboard works with: the status, attempt count and lease belong to a
/// group, not to the message. Every column comes from the task table — the message fields are
/// denormalised there — so a search does not join the message table at all.
/// </remarks>
public sealed record PgOutboxTaskEntry
{
    /// <summary>The stored message id (<c>msg_id</c>, a v7 UUID).</summary>
    public required Guid MessageId { get; init; }

    /// <summary>The database-assigned message order (<c>msg_seq</c>); the first half of the cursor.</summary>
    public required long MsgSeq { get; init; }

    /// <summary>The task id; the second half of the cursor.</summary>
    public required long TaskId { get; init; }

    /// <summary>The consumer group that owns the task.</summary>
    public required string ConsumerGroup { get; init; }

    /// <summary>The caller-supplied payload id (<c>msg_payload_id</c>). Not unique.</summary>
    public required string PayloadId { get; init; }

    /// <summary>The tenant that owns the message.</summary>
    public required int TenantId { get; init; }

    /// <summary>The message part.</summary>
    public required string Part { get; init; }

    /// <summary>When the message was stored.</summary>
    public required DateTimeOffset MessageCreatedAt { get; init; }

    /// <summary>When the group's loader created the task (the load lag is the difference).</summary>
    public required DateTimeOffset TaskCreatedAt { get; init; }

    /// <summary>The last reported delivery status.</summary>
    public required Delivery.DeliveryStatusCode StatusCode { get; init; }

    /// <summary>The free-text message written with <see cref="StatusCode"/>; empty when none.</summary>
    public required string StatusMessage { get; init; }

    /// <summary>The delivery attempt counter.</summary>
    public int Attempt { get; init; }

    /// <summary>When the last delivery finished, or <see langword="null"/> if none has finished yet.</summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>When the current lease expires; <see langword="null"/> means no lease has been taken.</summary>
    public DateTimeOffset? LockExpiresOn { get; init; }

    /// <summary>The <c>__error$</c> id, when the last outcome was an error.</summary>
    public long? ErrorId { get; init; }

    /// <summary>Whether the task is <c>Processing</c> with an expired lease.</summary>
    public bool IsStuck { get; init; }

    /// <summary>
    /// Whether no further delivery is expected for this group without a manual requeue. Mirrors the
    /// set the provider's lock-and-select query skips: everything except
    /// <c>Pending</c>/<c>Processing</c>/<c>Postpone</c>/<c>Retry</c>/<c>Warn</c>.
    /// </summary>
    public bool IsTerminal => StatusCode is not (
        Delivery.DeliveryStatusCode.Pending
        or Delivery.DeliveryStatusCode.Processing
        or Delivery.DeliveryStatusCode.Postpone
        or Delivery.DeliveryStatusCode.Retry
        or Delivery.DeliveryStatusCode.Warn);

    /// <summary>The cursor to pass as <see cref="PgOutboxTaskQuery.After"/> to fetch the next page.</summary>
    public PgOutboxSearchCursor Cursor => new(MsgSeq, TaskId);
}

/// <summary>
/// The position after which a search continues, as <c>(msg_seq, task_id)</c> of the last returned task.
/// </summary>
/// <remarks>
/// A message has one task per group, so <c>msg_seq</c> alone is not unique in the result. The pair is
/// ordered lexicographically by PostgreSQL's row comparison, which matches the
/// <c>ORDER BY msg_seq, task_id</c> of the query.
/// </remarks>
/// <param name="MsgSeq">The message order of the last returned task.</param>
/// <param name="TaskId">The task id of the last returned task.</param>
public readonly record struct PgOutboxSearchCursor(long MsgSeq, long TaskId);
