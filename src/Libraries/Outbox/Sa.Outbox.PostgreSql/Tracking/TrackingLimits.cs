namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// How much one tracking call may pull into memory.
/// </summary>
/// <remarks>
/// <para>
/// The read paths are diagnostic, but they are not free: <see cref="IPgOutboxMessageTracker.GetAsync"/>,
/// <see cref="IPgOutboxMessageTracker.GetManyAsync"/> and
/// <see cref="IPgOutboxMessageTracker.GetByMessageIdAsync"/> fan out to one row per (message, consumer
/// group) and nothing else bounds them — <see cref="IPgOutboxMessageTracker.SearchAsync"/> has
/// <see cref="PgOutboxTaskQuery.MaxLimit"/>, the trace reads had no cap at all. The cap is enforced
/// twice on purpose: as a <c>LIMIT</c> in the statement, so the row set never grows past it, and as a
/// check on the row count, so a result that hit the cap <b>fails</b> instead of being silently
/// truncated — a truncated trace reads as "no group has loaded this message", which is exactly the
/// diagnosis a caller came here for.
/// </para>
/// <para>
/// Use it as the upper bound of <see cref="IPgOutboxMessageTracker.GetHistoryAsync"/>'s
/// <c>limit</c>: anything above it is rejected with an <see cref="System.ArgumentOutOfRangeException"/>
/// rather than clamped, so an accidental dashboard "unlimited" is visible.
/// </para>
/// </remarks>
public static class TrackingLimits
{
    /// <summary>
    /// The most rows a single trace read (or one history page) may return: 5000.
    /// </summary>
    public const int MaxRows = 5000;
}
