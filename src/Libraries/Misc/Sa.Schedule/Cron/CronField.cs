namespace Sa.Schedule.Cron;

/// <summary>
/// The parsed content of a single cron field: its plain numeric values plus the
/// day-of-month / day-of-week specials that the token contained.
/// </summary>
internal sealed class CronField
{
    /// <summary>
    /// The human-readable field name used in error messages ("minute", "day-of-month", ...).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The raw token exactly as written in the expression.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// The numeric values of the field, normalized (day-of-week 7 is folded into 0),
    /// sorted and de-duplicated. Never empty — the parser fails fast on an empty field.
    /// </summary>
    public required int[] Values { get; init; }

    /// <summary>
    /// <c>"L"</c> — the last day of the month.
    /// </summary>
    public bool LastDay { get; init; }

    /// <summary>
    /// <c>"L-n"</c> — n days before the last day of the month. A zero offset is folded into <see cref="LastDay"/>.
    /// </summary>
    public required int[] LastDayOffsets { get; init; }

    /// <summary>
    /// <c>"LW"</c> — the last weekday (Monday-Friday) of the month.
    /// </summary>
    public bool LastWeekday { get; init; }

    /// <summary>
    /// <c>"dW"</c> — the weekday nearest to day <c>d</c>, never crossing a month boundary.
    /// </summary>
    public required int[] NearestWeekdays { get; init; }

    /// <summary>
    /// <c>"dL"</c> — the last <c>d</c>-weekday of the month (day-of-week field only).
    /// </summary>
    public required int[] LastWeekdaysOfMonth { get; init; }

    /// <summary>
    /// True when the field restricts days through a special instead of plain numeric values.
    /// </summary>
    public bool HasSpecials =>
        LastDay || LastWeekday
        || LastDayOffsets.Length > 0
        || NearestWeekdays.Length > 0
        || LastWeekdaysOfMonth.Length > 0;
}
