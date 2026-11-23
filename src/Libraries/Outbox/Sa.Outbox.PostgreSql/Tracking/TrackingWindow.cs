namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// The data window shared by every tracking and maintenance call.
/// </summary>
/// <remarks>
/// The window exists because the outbox tables are list-partitioned by tenant and by day: a query
/// without time bounds would visit every leaf of every tenant. It is a <b>data</b> window, not an
/// as-of snapshot — a message created inside it whose task rows were created after <c>To</c> reads
/// as "not loaded".
/// </remarks>
internal static class TrackingWindow
{
    /// <summary>
    /// The window applied when the caller passes neither bound. Seven days matches the outbox's
    /// default lookback (and therefore the default delivery horizon); an older message needs an
    /// explicit <c>fromDate</c> and, past the retention period, cannot be read at all.
    /// </summary>
    public static readonly TimeSpan DefaultLookback = TimeSpan.FromDays(7);

    public static (DateTimeOffset From, DateTimeOffset To) Resolve(
        TimeProvider timeProvider,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate)
    {
        DateTimeOffset end = toDate ?? timeProvider.GetUtcNow();
        DateTimeOffset start = fromDate ?? end - DefaultLookback;

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toDate),
                toDate,
                "The end of the tracking window must be later than its start.");
        }

        return (start, end);
    }
}
