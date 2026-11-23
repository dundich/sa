namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// Read-only view of the PostgreSQL storage: what happened to a published message.
/// </summary>
/// <remarks>
/// <para>
/// The tracker is a diagnostic API, not part of the delivery path. It reads the same tables the
/// provider writes — <c>__msg$</c> for the message itself, the task table for the per-group state,
/// <c>__log$</c> for the delivery history and <c>__error$</c> for the exception detail — and never
/// changes anything.
/// </para>
/// <para>
/// A message does not have one state: each consumer group that loads it gets its own task row, and
/// each delivery attempt appends a row to the log. <see cref="GetAsync(string, int, string?, DateTimeOffset?, DateTimeOffset?, CancellationToken)"/>
/// therefore returns every message stored under the payload id, each with one state per group.
/// </para>
/// <para>
/// Every method requires a tenant and applies a time window: the message table is list-partitioned
/// by <c>(tenant_id, msg_part)</c> and the task, delivery and error tables by
/// <c>(tenant_id, consumer_group)</c> and by day. A query without those predicates would scan every
/// leaf the outbox owns. The default window is the last seven days; rows older than
/// <see cref="Configuration.PgOutboxCleanupSettings.DropPartsAfterRetention"/> are dropped by
/// cleanup and can no longer be found, so a message older than the retention period reads as absent.
/// </para>
/// </remarks>
public interface IPgOutboxMessageTracker
{
    /// <summary>
    /// Returns every message stored under <paramref name="payloadId"/> with a state per consumer group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>msg_payload_id</c> is supplied by the publisher and is <b>not unique</b>, so a payload id
    /// published twice yields two entries. An empty (<c>string.Empty</c>) payload id is rejected: the
    /// provider uses it for messages without a payload id, and matching all of them would be useless.
    /// </para>
    /// <para>
    /// A message whose <see cref="PgOutboxMessageTrace.Deliveries"/> is empty has been stored but not
    /// loaded by any consumer group yet — that is not the same as <c>Pending</c>, which means
    /// "loaded and not rented". A state is only visible once its group's loader has created a task
    /// row, and a task row disappears only with the partition it lives in (retention cleanup).
    /// </para>
    /// </remarks>
    /// <param name="payloadId">The caller-supplied payload id to look up.</param>
    /// <param name="tenantId">The tenant the messages belong to; required for partition pruning.</param>
    /// <param name="part">The message part, when known. Narrows the scan to one part's leaves.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The messages in publish order, each with its per-group states.</returns>
    /// <exception cref="ArgumentException"><paramref name="payloadId"/> or <paramref name="part"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toDate"/> is not later than <paramref name="fromDate"/>.</exception>
    ValueTask<IReadOnlyList<PgOutboxMessageTrace>> GetAsync(
        string payloadId,
        int tenantId,
        string? part = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the messages stored under several payload ids in one query — the page-sized form of
    /// <see cref="GetAsync"/>.
    /// </summary>
    /// <remarks>
    /// Blank ids are ignored and duplicates are collapsed. The query is still bounded by the tenant and
    /// the window, so the cost is the number of matching daily leaves rather than the number of ids.
    /// </remarks>
    /// <param name="payloadIds">The caller-supplied payload ids to look up.</param>
    /// <param name="tenantId">The tenant the messages belong to; required for partition pruning.</param>
    /// <param name="part">The message part, when known. Narrows the scan to one part's leaves.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The matching messages in publish order, each with its per-group states.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payloadIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="part"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toDate"/> is not later than <paramref name="fromDate"/>.</exception>
    ValueTask<IReadOnlyList<PgOutboxMessageTrace>> GetManyAsync(
        IReadOnlyCollection<string> payloadIds,
        int tenantId,
        string? part = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the message with the given <c>msg_id</c>, or <see langword="null"/> when it is not in
    /// the window.
    /// </summary>
    /// <remarks>
    /// The id is the one the provider persists and reports through
    /// <see cref="Poison.PgOutboxPoisonMessage.OutboxId"/> or the logs; it is unique, so at most one
    /// message matches.
    /// </remarks>
    /// <param name="messageId">The message id (<c>msg_id</c>, a v7 UUID).</param>
    /// <param name="tenantId">The tenant the message belongs to; required for partition pruning.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The message with its per-group states, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toDate"/> is not later than <paramref name="fromDate"/>.</exception>
    ValueTask<PgOutboxMessageTrace?> GetByMessageIdAsync(
        Guid messageId,
        int tenantId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the consumer groups the storage has seen for a tenant, with their consumption cursors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A group gets a row in the offset table on its first poll, so this is the registry of groups that
    /// have actually started — a group registered in DI but never started is absent, and that absence
    /// is itself the diagnosis.
    /// </para>
    /// <para>
    /// Together with <see cref="PgOutboxMessageTrace.GetMissingGroups"/> this answers "has every group
    /// that should have received the message received it": the cursor shows how far a group has read,
    /// the task rows show which messages it has taken.
    /// </para>
    /// </remarks>
    /// <param name="tenantId">The tenant to list; required for partition pruning.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The groups ordered by id, or an empty list when no group has started.</returns>
    ValueTask<IReadOnlyList<PgOutboxConsumerGroup>> GetConsumerGroupsAsync(
        int tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the tenant's tasks per `(consumer group, status)` inside the window.
    /// </summary>
    /// <remarks>
    /// Reads the task table only, so it stays cheap for a metrics endpoint: no message join and no
    /// payload. <see cref="PgOutboxStatusCount.Stuck"/> folds in "Processing with an expired lease" on
    /// the same pass. A message not loaded by any group has no task row and is not counted anywhere —
    /// this is queue health, not a message audit.
    /// </remarks>
    /// <param name="tenantId">The tenant to count; required for partition pruning.</param>
    /// <param name="consumerGroup">The group to count, or <see langword="null"/> for every group of the tenant.</param>
    /// <param name="part">The message part, when known.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>One row per group and status, ordered by group then status.</returns>
    /// <exception cref="ArgumentException"><paramref name="consumerGroup"/> or <paramref name="part"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toDate"/> is not later than <paramref name="fromDate"/>.</exception>
    ValueTask<IReadOnlyList<PgOutboxStatusCount>> CountAsync(
        int tenantId,
        string? consumerGroup = null,
        string? part = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored payload of one message: the bytes exactly as the serializer wrote them, the
    /// payload type name and the recorded size.
    /// </summary>
    /// <param name="messageId">The message id (<c>msg_id</c>, a v7 UUID).</param>
    /// <param name="tenantId">The tenant the message belongs to; required for partition pruning.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The payload, or <see langword="null"/> when the message is not in the window.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toDate"/> is not later than <paramref name="fromDate"/>.</exception>
    ValueTask<PgOutboxMessagePayload?> GetPayloadAsync(
        Guid messageId,
        int tenantId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pages over the tenant's delivery tasks with optional status, group and part filters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unit is a <b>task</b>, not a message: status, attempts and lease belong to one consumer
    /// group, so a message delivered to three groups appears three times. This is the query behind a
    /// queue dashboard ("what is failing right now"), not a message audit — use
    /// <see cref="GetAsync(string, int, string?, DateTimeOffset?, DateTimeOffset?, CancellationToken)"/>
    /// when the payload id is known.
    /// </para>
    /// <para>
    /// Pagination is keyset on <c>(msg_seq, task_id)</c>: pass <see cref="PgOutboxTaskEntry.Cursor"/>
    /// of the last entry as <see cref="PgOutboxTaskQuery.After"/>. An empty <c>Statuses</c> collection
    /// matches nothing, and <see cref="PgOutboxTaskQuery.Limit"/> is capped at
    /// <see cref="PgOutboxTaskQuery.MaxLimit"/>.
    /// </para>
    /// <para>
    /// Ordering by <c>msg_seq</c> is a sort over the window: the task table has no index on that column.
    /// Combine the filters and a narrow window on large queues.
    /// </para>
    /// </remarks>
    /// <param name="query">The filters, window and page cursor.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>At most <see cref="PgOutboxTaskQuery.Limit"/> tasks, ordered by message sequence then task id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="PgOutboxTaskQuery.ConsumerGroup"/> or <see cref="PgOutboxTaskQuery.Part"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PgOutboxTaskQuery.Limit"/> is not between 1 and <see cref="PgOutboxTaskQuery.MaxLimit"/>,
    /// or the window end is not later than its start.
    /// </exception>
    ValueTask<IReadOnlyList<PgOutboxTaskEntry>> SearchAsync(
        PgOutboxTaskQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the delivery attempts of a payload id from the append-only <c>__log$</c> table, oldest
    /// first.
    /// </summary>
    /// <remarks>
    /// This is the only API that shows how a message changed over time: each finished delivery
    /// appends a row with the status the consumer reported. A lease that expires before the consumer
    /// finishes produces a second attempt for the same message, so the sequence is at-least-once, not
    /// a clean retry counter. Only the newest <paramref name="limit"/> attempts are kept.
    /// </remarks>
    /// <param name="payloadId">The caller-supplied payload id to look up.</param>
    /// <param name="tenantId">The tenant the messages belong to; required for partition pruning.</param>
    /// <param name="consumerGroup">The consumer group to read, or <see langword="null"/> for all groups.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="limit">The maximum number of attempts to return, newest first before the result is ordered.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The attempts in chronological order, or an empty list when there are none.</returns>
    /// <exception cref="ArgumentException"><paramref name="payloadId"/> or <paramref name="consumerGroup"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toDate"/> is not later than <paramref name="fromDate"/>, or <paramref name="limit"/> is not positive.</exception>
    ValueTask<IReadOnlyList<PgOutboxDeliveryAttempt>> GetHistoryAsync(
        string payloadId,
        int tenantId,
        string? consumerGroup = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int limit = 500,
        CancellationToken cancellationToken = default);
}
