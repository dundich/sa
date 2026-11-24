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
        // `toDate` absent means "up to now"; `fromDate` absent means "a lookback from the end". Both
        // bounds are resolved before anything is compared, so the error below can name the one the
        // caller actually supplied instead of always blaming `toDate` (which may not have been
        // passed at all — the common mistake is a `fromDate` in the future).
        DateTimeOffset end = toDate ?? timeProvider.GetUtcNow();
        DateTimeOffset start = fromDate ?? Floor(end, DefaultLookback);

        if (start >= end)
        {
            throw toDate is null
                ? new ArgumentOutOfRangeException(
                    nameof(fromDate),
                    fromDate,
                    "The start of the tracking window must be earlier than its end, which defaults "
                    + "to the current time when toDate is not supplied.")
                : new ArgumentOutOfRangeException(
                    nameof(toDate),
                    toDate,
                    "The end of the tracking window must be later than its start.");
        }

        return (start, end);
    }

    /// <summary>
    /// <c>value - delta</c>, clamped at <see cref="DateTimeOffset.MinValue"/> instead of throwing.
    /// </summary>
    /// <remarks>
    /// Both subtractions on this path (<c>end - lookback</c> and the error row's one-day slack) are
    /// arithmetic on caller-supplied bounds: near <see cref="DateTimeOffset.MinValue"/> the plain
    /// operator throws an <see cref="ArgumentOutOfRangeException"/> whose parameter is the operand,
    /// which names nothing the caller passed.
    /// <para>
    /// The arithmetic runs on <see cref="DateTimeOffset.UtcTicks"/>, not on <see cref="DateTimeOffset.Ticks"/>:
    /// the latter is the clock reading, and an offset is re-validated against the UTC range, so a value
    /// a few hours into year 1 with a positive offset throws even when its clock part is in range.
    /// The offset itself is preserved on the result.
    /// </para>
    /// </remarks>
    public static DateTimeOffset Floor(DateTimeOffset value, TimeSpan delta)
    {
        long utc = value.UtcTicks - delta.Ticks;

        if (utc < DateTimeOffset.MinValue.UtcTicks) return DateTimeOffset.MinValue;

        return new DateTimeOffset(utc, TimeSpan.Zero).ToOffset(value.Offset);
    }
}
