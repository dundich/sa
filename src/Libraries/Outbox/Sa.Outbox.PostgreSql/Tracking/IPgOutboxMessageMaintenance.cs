namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// Write operations on already-stored messages: requeueing a settled delivery.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="IPgOutboxMessageTracker"/> on purpose: the tracker is a read-only view,
/// and a caller granted "look at the outbox" should not silently also hold "make it deliver again".
/// </para>
/// <para>
/// A requeue does not touch the group's cursor and does not re-insert anything. It resets the existing
/// task row to <see cref="Delivery.DeliveryStatusCode.Pending"/>, so the next rent of that group picks
/// the message up again. The append-only delivery log keeps both the earlier and the new attempts.
/// </para>
/// </remarks>
public interface IPgOutboxMessageMaintenance
{
    /// <summary>
    /// Resets the settled deliveries of <paramref name="payloadId"/> to <c>Pending</c> so the
    /// consumer groups deliver the message again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only settled tasks are touched: <c>Pending</c>, <c>Processing</c>, <c>Postpone</c>,
    /// <c>Retry</c> and <c>Warn</c> are still owned by the delivery pipeline, and resetting them would
    /// either steal a live lease or cancel a scheduled retry. A message whose task rows were never
    /// created (no group has loaded it) matches nothing — it will be delivered on its own.
    /// </para>
    /// <para>
    /// The requeue works only inside the group's delivery lookback
    /// (<see cref="Delivery.OutboxConsumerSettings.LookbackInterval"/>, seven days by default): the
    /// rent query filters on the message's creation time, so an older message is reset but never
    /// rented again. Rows past the retention period are gone with their partitions.
    /// </para>
    /// <para>
    /// Delivery stays at-least-once: a consumer that already processed the message will see it again
    /// and must be idempotent.
    /// </para>
    /// </remarks>
    /// <param name="payloadId">The caller-supplied payload id to requeue.</param>
    /// <param name="tenantId">The tenant the messages belong to; required for partition pruning.</param>
    /// <param name="consumerGroup">The group to reset, or <see langword="null"/> for every group of the tenant.</param>
    /// <param name="part">The message part, when known; narrows the match.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of task rows reset.</returns>
    /// <exception cref="ArgumentException"><paramref name="payloadId"/>, <paramref name="consumerGroup"/> or <paramref name="part"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolved window is empty: <paramref name="fromDate"/>
    /// (or the default seven-day lookback from <paramref name="toDate"/>) is not earlier than
    /// <paramref name="toDate"/> (or the current time). The parameter named in the exception is the
    /// one the caller supplied.</exception>
    ValueTask<int> RequeueAsync(
        string payloadId,
        int tenantId,
        string? consumerGroup = null,
        string? part = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the settled deliveries of the message with the given <c>msg_id</c> to <c>Pending</c>.
    /// </summary>
    /// <remarks>Same semantics and limits as <see cref="RequeueAsync"/>; the id is the one the provider persists.</remarks>
    /// <param name="messageId">The message id (<c>msg_id</c>, a v7 UUID).</param>
    /// <param name="tenantId">The tenant the message belongs to; required for partition pruning.</param>
    /// <param name="consumerGroup">The group to reset, or <see langword="null"/> for every group of the tenant.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of task rows reset.</returns>
    /// <exception cref="ArgumentException"><paramref name="consumerGroup"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolved window is empty: <paramref name="fromDate"/>
    /// (or the default seven-day lookback from <paramref name="toDate"/>) is not earlier than
    /// <paramref name="toDate"/> (or the current time). The parameter named in the exception is the
    /// one the caller supplied.</exception>
    ValueTask<int> RequeueByMessageIdAsync(
        Guid messageId,
        int tenantId,
        string? consumerGroup = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets one task to <c>Pending</c> by its surrogate id.
    /// </summary>
    /// <remarks>
    /// The task id is what <see cref="Poison.PgOutboxPoisonMessage.TaskId"/> reports, and the task
    /// table's primary key starts with it, which makes this the cheapest requeue: no payload-id scan.
    /// Same semantics and limits as <see cref="RequeueAsync"/>.
    /// </remarks>
    /// <param name="taskId">The task id (<c>task_id</c>).</param>
    /// <param name="tenantId">The tenant the task belongs to; required for partition pruning.</param>
    /// <param name="fromDate">Start of the data window (inclusive). Defaults to seven days before <paramref name="toDate"/>.</param>
    /// <param name="toDate">End of the data window (inclusive). Defaults to the current time.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of task rows reset (0 or 1).</returns>
    /// <exception cref="ArgumentOutOfRangeException">The resolved window is empty: <paramref name="fromDate"/>
    /// (or the default seven-day lookback from <paramref name="toDate"/>) is not earlier than
    /// <paramref name="toDate"/> (or the current time). The parameter named in the exception is the
    /// one the caller supplied.</exception>
    ValueTask<int> RequeueTaskAsync(
        long taskId,
        int tenantId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);
}
