namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// The aggregate state of one message across the consumer groups that have loaded it.
/// </summary>
/// <remarks>
/// A message has one state <em>per group</em>; this value is the documented worst-of summary of those
/// states, so callers do not each invent their own precedence. It says nothing about groups that have
/// not loaded the message yet — use <see cref="PgOutboxMessageTrace.GetMissingGroups"/> together with
/// the registry from <see cref="IPgOutboxMessageTracker.GetConsumerGroupsAsync"/> for that.
/// </remarks>
public enum PgOutboxMessageStage
{
    /// <summary>Stored, but no consumer group has loaded it yet. This is not <c>Pending</c>.</summary>
    NotLoaded = 0,

    /// <summary>At least one group has delivery work scheduled and no group has failed permanently.</summary>
    InFlight = 1,

    /// <summary>
    /// At least one group ended in a permanent error (5xx / max attempts). Failure outranks an
    /// in-flight retry in another group because it is the state that needs attention.
    /// </summary>
    Failed = 2,

    /// <summary>Every group that loaded the message ended in a terminal outcome without an error.</summary>
    Settled = 3,
}
