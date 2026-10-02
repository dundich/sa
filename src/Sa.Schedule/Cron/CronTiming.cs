namespace Sa.Schedule.Cron;

/// <summary>
/// Implements cron-based scheduling using standard 5-field cron expressions.
/// When both day-of-month and day-of-week are restricted, a day matches when
/// either field matches (standard vixie-cron OR semantics).
/// The next occurrence is searched within a 28-year horizon — the full Gregorian
/// day-of-week cycle — so every satisfiable 5-field expression is found; null means
/// the expression can never match (e.g. February 30).
/// Optimized with O(1) membership tests and precomputed jump tables (.NET 8–10).
/// </summary>
public sealed class CronTiming
{
    private const string DefaultName = "cron";

    /// <summary>
    /// Search horizon in years. 28 years = 10,227 days = exactly 1,461 weeks — the full
    /// Gregorian date→day-of-week cycle (non-century years) — so any satisfiable
    /// 5-field expression fires within this window.
    /// </summary>
    private const int MaxSearchYears = 28;

    // Flag arrays for O(1) membership checks
    private readonly bool[] _minuteFlags = new bool[60];
    private readonly bool[] _hourFlags = new bool[24];
    private readonly bool[] _domFlags = new bool[32];   // index 1..31
    private readonly bool[] _monthFlags = new bool[13];   // index 1..12
    private readonly bool[] _dowFlags = new bool[7];    // 0=Sunday..6=Saturday

    // Lookup tables: next valid value >= index (sentinel = -1)
    private readonly int[] _nextMinute = new int[61];
    private readonly int[] _nextHour = new int[25];
    private readonly int[] _nextMonth = new int[14];

    // First valid value in each field (used as default when jumping)
    private readonly int _firstMinute;

    private readonly int _firstMonth;

    private readonly bool _dowWildcard;
    private readonly bool _domWildcard;

    // Day-of-month specials: "L", "LW" and "L-n" (index = n days before the month end)
    private readonly bool _domHasSpecials;
    private readonly bool _domLastDay;
    private readonly bool _domLastWeekday;
    private readonly bool[] _domLastDayOffsets = new bool[MaxLastDayOffset + 1];

    // "dW" — usually a single entry, matched against the real calendar of the candidate day
    private readonly int[] _nearestWeekdays;

    // Day-of-week special: "dL" — the last d-weekday of the month
    private readonly bool _dowHasSpecials;
    private readonly bool[] _dowLastOfMonth = new bool[7];

    private const int MaxLastDayOffset = 30;

    public string TimingName { get; }

    /// <summary>
    /// The cron expression this timing was created from.
    /// </summary>
    public string Expression { get; }

    /// <summary>
    /// Creates a cron timing from a 5-field expression: "minute hour day-of-month month day-of-week".
    /// </summary>
    /// <param name="expression">The cron expression.</param>
    /// <param name="name">Optional display name, defaults to "cron".</param>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
    /// <exception cref="CronParseException">The expression is malformed.</exception>
    public CronTiming(string expression, string? name = null)
        : this(expression, name, strictCalendarValidation: false)
    {
    }

    private CronTiming(string expression, string? name, bool strictCalendarValidation)
    {
        ArgumentNullException.ThrowIfNull(expression);

        Expression = expression;
        TimingName = name ?? DefaultName;

        var fields = SplitExpression(expression);

        var minute = CronFieldParser.Parse(fields[(int)CronFieldKind.Minute], CronFieldKind.Minute);
        var hour = CronFieldParser.Parse(fields[(int)CronFieldKind.Hour], CronFieldKind.Hour);
        var dayOfMonth = CronFieldParser.Parse(fields[(int)CronFieldKind.DayOfMonth], CronFieldKind.DayOfMonth);
        var month = CronFieldParser.Parse(fields[(int)CronFieldKind.Month], CronFieldKind.Month);
        var dayOfWeek = CronFieldParser.Parse(fields[(int)CronFieldKind.DayOfWeek], CronFieldKind.DayOfWeek);

        _nearestWeekdays = dayOfMonth.NearestWeekdays;

        // Minute
        PopulateFlags(minute.Values, _minuteFlags);
        _firstMinute = BuildNextTable(_minuteFlags, _nextMinute, 0, 59);

        // Hour
        PopulateFlags(hour.Values, _hourFlags);
        BuildNextTable(_hourFlags, _nextHour, 0, 23);

        // Day-of-month (no jump table needed, only flags)
        PopulateFlags(dayOfMonth.Values, _domFlags);

        // Month
        PopulateFlags(month.Values, _monthFlags);
        _firstMonth = BuildNextTable(_monthFlags, _nextMonth, 1, 12);

        // Day-of-week
        PopulateFlags(dayOfWeek.Values, _dowFlags);

        // Day-of-month specials
        _domHasSpecials = dayOfMonth.HasSpecials;
        _domLastDay = dayOfMonth.LastDay;
        _domLastWeekday = dayOfMonth.LastWeekday;

        foreach (int offset in dayOfMonth.LastDayOffsets)
            _domLastDayOffsets[offset] = true;

        // Day-of-week specials
        _dowHasSpecials = dayOfWeek.LastWeekdaysOfMonth.Length > 0;

        foreach (int day in dayOfWeek.LastWeekdaysOfMonth)
            _dowLastOfMonth[day] = true;

        // Wildcard detection — a field that restricts days through a special is never a wildcard
        _dowWildcard = !_dowHasSpecials && AreAllFlagsSet(_dowFlags, 0, 6);
        _domWildcard = !_domHasSpecials && AreAllFlagsSet(_domFlags, 1, 31);

        // A restricted day-of-week makes every day match on its own, so the calendar
        // combination of day-of-month and month is irrelevant in that case.
        if (strictCalendarValidation && _dowWildcard)
            ValidateSatisfiable(dayOfMonth, month);
    }

    /// <summary>
    /// Creates a cron timing with the given expression and display name.
    /// </summary>
    public static CronTiming Every(string expression, string? name = null) =>
        new(expression, name);

    /// <summary>
    /// Creates a cron timing that additionally validates the expression against the real calendar,
    /// rejecting day-of-month values that cannot occur in the selected month — for example
    /// <c>0 0 30 2 *</c> (February 30) or <c>0 0 31 4 *</c> (April 31).
    /// <para>
    /// Without this check such an expression is accepted and
    /// <see cref="GetNextOccurrence"/> keeps returning null, which silently aborts the job loop.
    /// Day-of-month 29 in February stays valid because a leap year occurs inside the search horizon.
    /// </para>
    /// </summary>
    /// <param name="expression">The cron expression.</param>
    /// <param name="name">Optional display name, defaults to "cron".</param>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
    /// <exception cref="CronParseException">The expression is malformed or can never match a date.</exception>
    public static CronTiming Strict(string expression, string? name = null) =>
        new(expression, name, strictCalendarValidation: true);

    /// <inheritdoc/>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset dateTime)
    {
        var candidate = TruncateToMinute(dateTime.AddMinutes(1));
        var maxSearch = dateTime.AddYears(MaxSearchYears);

        while (candidate <= maxSearch)
        {
            if (Matches(candidate))
                return candidate;
            candidate = Advance(candidate, maxSearch);
        }
        return null;
    }

    private bool Matches(DateTimeOffset dt) =>
        _monthFlags[dt.Month] &&
        MatchDay(dt) &&
        _hourFlags[dt.Hour] &&
        _minuteFlags[dt.Minute];

    private bool MatchDay(DateTimeOffset dt)
    {
        bool domMatch = MatchDayOfMonth(dt);
        bool dowMatch = MatchDayOfWeek(dt);

        if (!_domWildcard && !_dowWildcard)
            return domMatch || dowMatch;  // Both restricted → either must match (standard cron OR semantics)
        if (_domWildcard && _dowWildcard)
            return true;                   // No restrictions → any day
        return _domWildcard ? dowMatch : domMatch;
    }

    private bool MatchDayOfMonth(DateTimeOffset dt)
    {
        int day = dt.Day;

        if (_domFlags[day])
            return true;

        if (!_domHasSpecials)
            return false;

        int daysInMonth = DateTime.DaysInMonth(dt.Year, dt.Month);

        if (day == daysInMonth && (_domLastDay || (_domLastWeekday && IsWeekday(dt.DayOfWeek))))
            return true;

        // "L-n" is an offset backwards from the month end; the offset never exceeds 30 days.
        if (_domLastDayOffsets[daysInMonth - day])
            return true;

        foreach (int target in _nearestWeekdays)
        {
            if (IsNearestWeekday(dt, target, daysInMonth))
                return true;
        }

        return false;
    }

    private bool MatchDayOfWeek(DateTimeOffset dt)
    {
        var dayOfWeek = (int)dt.DayOfWeek;

        if (_dowFlags[dayOfWeek])
            return true;

        if (!_dowHasSpecials)
            return false;

        // "dL" can only land in the last seven days of the month.
        return dt.Day > DateTime.DaysInMonth(dt.Year, dt.Month) - 7 && _dowLastOfMonth[dayOfWeek];
    }

    /// <summary>
    /// Implements the Quartz "W" rule: the weekday nearest to the target day, never crossing
    /// a month boundary. Saturday normally resolves to the Friday before it, but a Saturday on
    /// the 1st resolves to the Monday on the 3rd; Sunday normally resolves to the Monday after
    /// it, but a Sunday on the last day resolves to the Friday before it. A target day that the
    /// month does not have never matches.
    /// </summary>
    private static bool IsNearestWeekday(DateTimeOffset dt, int targetDay, int daysInMonth)
    {
        if (targetDay > daysInMonth)
            return false;

        var target = new DateTimeOffset(dt.Year, dt.Month, targetDay, 0, 0, 0, dt.Offset);

        return target.DayOfWeek switch
        {
            DayOfWeek.Saturday => dt.Day == (targetDay > 1 ? targetDay - 1 : targetDay + 2),
            DayOfWeek.Sunday => dt.Day == (targetDay < daysInMonth ? targetDay + 1 : targetDay - 2),
            _ => dt.Day == targetDay,
        };
    }

    private static bool IsWeekday(DayOfWeek dayOfWeek) =>
        dayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    private DateTimeOffset Advance(DateTimeOffset dt, DateTimeOffset horizon)
    {
        // 1. Try later minute this hour
        int nextMin = dt.Minute + 1;
        if (nextMin <= 59)
        {
            int m = _nextMinute[nextMin];
            if (m != -1)
                return new DateTimeOffset(dt.Year, dt.Month, dt.Day, dt.Hour, m, 0, dt.Offset);
        }

        // 2. Try later hour today (with first valid minute)
        int nextH = dt.Hour + 1;
        if (nextH <= 23)
        {
            int h = _nextHour[nextH];
            if (h != -1)
                return new DateTimeOffset(dt.Year, dt.Month, dt.Day, h, _firstMinute, 0, dt.Offset);
        }

        // 3. Jump to tomorrow and search forward up to the horizon
        var nextDay = new DateTimeOffset(dt.Year, dt.Month, dt.Day, 0, 0, 0, dt.Offset).AddDays(1);
        return FindEarliestOnOrAfter(nextDay, horizon);
    }

    private DateTimeOffset FindEarliestOnOrAfter(DateTimeOffset from, DateTimeOffset horizon)
    {
        // Empty search window — return the start point; the caller still advances
        // by at least a day, so the outer loop terminates.
        if (from > horizon)
            return from;

        var current = from;

        while (current <= horizon)
        {
            // Month skip
            if (!_monthFlags[current.Month])
            {
                int nm = _nextMonth[current.Month + 1];
                current = nm != -1
                    ? new DateTimeOffset(current.Year, nm, 1, 0, 0, 0, current.Offset)
                    : new DateTimeOffset(current.Year + 1, _firstMonth, 1, 0, 0, 0, current.Offset);
                continue;
            }

            // Day skip
            if (!MatchDay(current))
            {
                current = current.AddDays(1);
                continue;
            }

            // First valid hour today
            int hour = _nextHour[current.Hour];
            if (hour == -1)
            {
                current = current.AddDays(1);
                continue;
            }

            // First valid minute in that hour
            int minute = _nextMinute[current.Minute];
            if (minute != -1)
                return new DateTimeOffset(current.Year, current.Month, current.Day, hour, minute, 0, current.Offset);

            // No minute at this hour → try next valid hour
            int nextHour = _nextHour[hour + 1];
            if (nextHour != -1)
                return new DateTimeOffset(current.Year, current.Month, current.Day, nextHour, _firstMinute, 0, current.Offset);

            current = current.AddDays(1);
        }

        // No match within the horizon. Jump the caller to the end of the horizon —
        // a guaranteed forward step (the previous candidate is strictly before it),
        // so no valid occurrence is skipped and the search never degrades into a
        // day-by-day re-walk of the same window.
        return horizon;
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Offset);

    // -------------------------------------------------------
    // Strict calendar validation
    // -------------------------------------------------------

    /// <summary>
    /// Fails fast when the day-of-month field has no valid day in any month of the expression:
    /// such an expression parses fine but silently aborts the job loop on the first
    /// <see cref="GetNextOccurrence"/> returning null.
    /// </summary>
    private static void ValidateSatisfiable(CronField dayOfMonth, CronField month)
    {
        foreach (int m in month.Values)
        {
            if (m is >= 1 and <= 12 && CanMatchDay(dayOfMonth, MaxDaysInMonth(m)))
                return;
        }

        throw new CronParseException(
            $"The {dayOfMonth.Name} field '{dayOfMonth.Token}' has no valid day in any month of the expression.",
            dayOfMonth.Name,
            dayOfMonth.Token);
    }

    private static bool CanMatchDay(CronField dayOfMonth, int daysInMonth)
    {
        foreach (int day in dayOfMonth.Values)
        {
            if (day <= daysInMonth)
                return true;
        }

        if (dayOfMonth.LastDay || dayOfMonth.LastWeekday)
            return true;

        foreach (int offset in dayOfMonth.LastDayOffsets)
        {
            if (daysInMonth - offset >= 1)
                return true;
        }

        foreach (int day in dayOfMonth.NearestWeekdays)
        {
            if (day <= daysInMonth)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The number of days the month has in a leap year — February 29 has to stay reachable,
    /// otherwise a valid "29 2" expression would be rejected.
    /// </summary>
    private static int MaxDaysInMonth(int month) => month == 2 ? 29 : DateTime.DaysInMonth(LeapYear, month);

    private const int LeapYear = 2024;

    // -------------------------------------------------------
    // Initialization helpers
    // -------------------------------------------------------

    private static void PopulateFlags(int[] values, bool[] flags)
    {
        foreach (int value in values)
        {
            if ((uint)value < (uint)flags.Length)
                flags[value] = true;
        }
    }

    /// <summary>Fills the jump table and returns the smallest valid value.</summary>
    private static int BuildNextTable(bool[] flags, int[] next, int min, int max)
    {
        int lastValid = -1;
        for (int i = max; i >= min; i--)
        {
            if (flags[i]) lastValid = i;
            next[i] = lastValid;
        }
        next[max + 1] = -1;
        for (int i = 0; i < min; i++) next[i] = -1;

        // Return the smallest valid value
        for (int i = min; i <= max; i++)
            if (flags[i])
                return i;
        return -1; // no valid value (should not happen for well-formed expressions)
    }

    private static bool AreAllFlagsSet(bool[] flags, int min, int max)
    {
        for (int i = min; i <= max; i++)
            if (!flags[i]) return false;
        return true;
    }

    // -------------------------------------------------------
    // Expression
    // -------------------------------------------------------

    private static string[] SplitExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new CronParseException("Cron expression cannot be null or empty.", "expression", expression);

        var fields = expression.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length != 5)
            throw new CronParseException(
                $"Cron expression must have exactly 5 fields, got {fields.Length}.", "expression", expression);

        return fields;
    }
}
