namespace Sa.Schedule.Engine;

using System;
using System.Collections.Generic;

/// <summary>
/// Implements cron-based scheduling using standard 5-field cron expressions.
/// Optimized with O(1) membership tests and precomputed jump tables (.NET 8–10).
/// </summary>
internal sealed class CronTiming : IJobTiming
{
    private const string DefaultName = "cron";

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

    public string TimingName { get; }

    public CronTiming(string expression, string? name = null)
    {
        TimingName = name ?? DefaultName;
        var fields = ParseExpression(expression);

        // Minute
        PopulateFlags(fields[0], _minuteFlags, 0, 59);
        _firstMinute = BuildNextTable(_minuteFlags, _nextMinute, 0, 59);

        // Hour
        PopulateFlags(fields[1], _hourFlags, 0, 23);

        // Day-of-month (no jump table needed, only flags)
        PopulateFlags(fields[2], _domFlags, 1, 31);

        // Month
        PopulateFlags(fields[3], _monthFlags, 1, 12);
        _firstMonth = BuildNextTable(_monthFlags, _nextMonth, 1, 12);

        // Day-of-week
        PopulateFlags(fields[4], _dowFlags, 0, 6);

        // Wildcard detection
        _dowWildcard = AreAllFlagsSet(_dowFlags, 0, 6);
        _domWildcard = AreAllFlagsSet(_domFlags, 1, 31);
    }

    public static CronTiming Every(string expression, string? name = null)
        => new(expression, name);

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset dateTime, IJobContext context)
    {
        var candidate = TruncateToMinute(dateTime.AddMinutes(1));
        var maxSearch = dateTime.AddYears(2);

        while (candidate <= maxSearch)
        {
            if (Matches(candidate))
                return candidate;
            candidate = Advance(candidate);
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
        bool domMatch = _domFlags[dt.Day];              // 1..31
        bool dowMatch = _dowFlags[(int)dt.DayOfWeek];   // 0..6

        if (!_domWildcard && !_dowWildcard)
            return domMatch && dowMatch;   // Both restricted → both must match
        if (_domWildcard && _dowWildcard)
            return true;                   // No restrictions → any day
        return _domWildcard ? dowMatch : domMatch;
    }

    private DateTimeOffset Advance(DateTimeOffset dt)
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

        // 3. Jump to tomorrow
        var nextDay = new DateTimeOffset(dt.Year, dt.Month, dt.Day, 0, 0, 0, dt.Offset).AddDays(1);
        return FindEarliestOnOrAfter(nextDay);
    }

    private DateTimeOffset FindEarliestOnOrAfter(DateTimeOffset from)
    {
        var maxYear = from.AddYears(2);
        var current = from;

        while (current <= maxYear)
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
        return from; // fallback (never reached)
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Offset);

    // -------------------------------------------------------
    // Initialization helpers
    // -------------------------------------------------------
    private static void PopulateFlags(int[] values, bool[] flags, int min, int max)
    {
        foreach (var v in from int v in values
                          where v >= min && v <= max
                          select v)
        {
            flags[v] = true;
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
    // Parser (unchanged)
    // -------------------------------------------------------
    private static int[][] ParseExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new FormatException("WithCron expression cannot be null or empty.");

        var fields = expression.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException($"WithCron expression must have exactly 5 fields, got {fields.Length}.");

        return
        [
            ParseField(fields[0], "minute", 0, 59),
            ParseField(fields[1], "hour", 0, 23),
            ParseField(fields[2], "day-of-month", 1, 31),
            ParseField(fields[3], "month", 1, 12),
            ParseField(fields[4], "day-of-week", 0, 6),
        ];
    }

    private static int[] ParseField(string field, string name, int min, int max)
    {
        var values = new List<int>();

        if (field == "*")
        {
            for (int i = min; i <= max; i++) values.Add(i);
        }
        else if (field.Contains('/'))
        {
            ParseStep(field, name, min, max, values);
        }
        else if (field.Contains('-'))
        {
            ParseRange(field, name, min, max, values);
        }
        else if (field.Contains(','))
        {
            ParseList(field, name, min, max, values);
        }
        else if (int.TryParse(field, out int v))
        {
            Validate(v, min, max, name);
            values.Add(v);
        }
        else
        {
            throw new FormatException($"Invalid cron field '{field}' for {name}.");
        }

        values.Sort();
        return [.. values];
    }

    private static void ParseStep(string field, string name, int min, int max, List<int> values)
    {
        var parts = field.Split('/', 2);
        if (parts.Length != 2) throw new FormatException($"Invalid step '{field}' for {name}.");

        int start, end;
        if (parts[0] == "*")
        {
            start = min; end = max;
        }
        else if (parts[0].Contains('-'))
        {
            var rp = parts[0].Split('-', 2);
            if (rp.Length != 2 || !int.TryParse(rp[0], out start) || !int.TryParse(rp[1], out end))
                throw new FormatException($"Invalid range/step '{parts[0]}' for {name}.");
            start = Validate(start, min, max, name);
            end = Validate(end, min, max, name);
        }
        else if (int.TryParse(parts[0], out int s))
        {
            start = Validate(s, min, max, name);
            end = max;
        }
        else
        {
            throw new FormatException($"Invalid step start '{parts[0]}' for {name}.");
        }

        if (!int.TryParse(parts[1], out int step) || step <= 0)
            throw new FormatException($"Invalid step value '{parts[1]}' for {name}.");

        for (int i = start; i <= end; i += step)
            values.Add(i);
    }

    private static void ParseRange(string field, string name, int min, int max, List<int> values)
    {
        var parts = field.Split('-', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int s) || !int.TryParse(parts[1], out int e))
            throw new FormatException($"Invalid range '{field}' for {name}.");

        s = Validate(s, min, max, name);
        e = Validate(e, min, max, name);
        if (s > e) throw new FormatException($"Range {s}-{e} invalid for {name}.");
        for (int i = s; i <= e; i++) values.Add(i);
    }

    private static void ParseList(string field, string name, int min, int max, List<int> values)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int v))
                values.Add(Validate(v, min, max, name));
            else
                throw new FormatException($"Invalid value '{part}' in list for {name}.");
        }
    }

    private static int Validate(int v, int min, int max, string name)
    {
        if (v < min || v > max) throw new FormatException($"Value {v} out of range [{min}-{max}] for {name}.");
        return v;
    }
}
