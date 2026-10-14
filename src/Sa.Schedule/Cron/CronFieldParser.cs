using System.Globalization;

namespace Sa.Schedule.Cron;

/// <summary>
/// Parses a single cron field into its numeric values and day specials.
/// Throws <see cref="CronParseException"/> on anything malformed — a field that cannot produce
/// values must fail at construction time instead of turning into a timing that never fires.
/// </summary>
internal static class CronFieldParser
{
    /// <summary>Maximum accepted "L-n" offset; a month never has more than 31 days.</summary>
    private const int MaxLastDayOffset = 30;

    private static readonly string[] FieldNames = ["minute", "hour", "day-of-month", "month", "day-of-week"];
    private static readonly int[] FieldMin = [0, 0, 1, 1, 0];
    private static readonly int[] FieldMax = [59, 23, 31, 12, 7];

    public static CronField Parse(string token, CronFieldKind kind)
    {
        string name = FieldNames[(int)kind];
        var acc = new Accumulator();

        if (token == "*")
        {
            int min = FieldMin[(int)kind];
            int max = FieldMax[(int)kind];

            for (int value = min; value <= max; value++)
                acc.Values.Add(value);
        }
        else if (token.Contains(','))
        {
            ParseList(token, kind, name, acc);
        }
        else if (token.Contains('/'))
        {
            ParseStep(token, kind, name, acc);
        }
        else
        {
            ParseSingle(token, kind, name, acc);
        }

        return Build(token, name, kind, acc);
    }

    // -------------------------------------------------------
    // Field shapes
    // -------------------------------------------------------

    /// <summary>
    /// A single token: a day special ("L", "L-3", "LW", "15W", "5L"), a range ("1-5") or a value ("0").
    /// Specials are probed first — "L-3" would otherwise be mistaken for a range.
    /// </summary>
    private static void ParseSingle(string token, CronFieldKind kind, string name, Accumulator acc)
    {
        if (TryParseSpecial(token, kind, name, acc))
            return;

        if (token.Contains('-'))
        {
            ParseRange(token, kind, name, acc);
            return;
        }

        if (TryParseValue(token, kind, name, out int value))
        {
            acc.Values.Add(value);
            return;
        }

        throw new CronParseException($"Invalid cron field '{token}' for {name}.", name, token);
    }

    private static void ParseList(string token, CronFieldKind kind, string name, Accumulator acc)
    {
        foreach (var element in token.Split(','))
        {
            var part = element.Trim();

            if (part.Length == 0)
                throw new CronParseException($"Empty value in list '{token}' for {name}.", name, element);

            // A list element may be any single field shape, not just a plain
            // number — standard cron allows steps ("0,*/15", "1-5/10", "5/10"),
            // ranges ("0-4,8-12") and "*" inside a list too. Each of those is
            // parsed exactly like it would be as a whole field.
            if (part.Contains('/'))
            {
                ParseStep(part, kind, name, acc);
                continue;
            }

            if (part == "*")
            {
                for (int v = FieldMin[(int)kind]; v <= FieldMax[(int)kind]; v++)
                    acc.Values.Add(v);
                continue;
            }

            if (TryParseValue(part, kind, name, out int value))
            {
                acc.Values.Add(value);
                continue;
            }

            // "L-3"-style specials look like ranges, so specials are probed first.
            if (TryParseSpecial(part, kind, name, acc))
                continue;

            if (part.Contains('-'))
            {
                ParseRange(part, kind, name, acc);
                continue;
            }

            throw new CronParseException($"Invalid value '{part}' in list for {name}.", name, part);
        }
    }

    private static void ParseStep(string token, CronFieldKind kind, string name, Accumulator acc)
    {
        var parts = token.Split('/', 2);
        if (parts.Length != 2)
            throw new CronParseException($"Invalid step '{token}' for {name}.", name, token);

        string rangeToken = parts[0];
        string stepToken = parts[1];

        if (ContainsSpecial(rangeToken) || ContainsSpecial(stepToken))
            throw new CronParseException(
                $"The 'L' and 'W' specials cannot be used in a step for {name}.", name, token);

        int min = FieldMin[(int)kind];
        int max = FieldMax[(int)kind];
        int start;
        int end;

        if (rangeToken == "*")
        {
            start = min;
            end = max;
        }
        else if (rangeToken.Contains('-'))
        {
            var rangeParts = rangeToken.Split('-', 2);
            if (rangeParts.Length != 2
                || !TryParseNumber(rangeParts[0], out start)
                || !TryParseNumber(rangeParts[1], out end))
                throw new CronParseException($"Invalid range/step '{rangeToken}' for {name}.", name, rangeToken);

            start = ValidateRange(start, min, max, name, rangeToken);
            end = ValidateRange(end, min, max, name, rangeToken);

            if (start > end)
                throw new CronParseException($"Range {start}-{end} invalid for {name}.", name, rangeToken);
        }
        else if (TryParseNumber(rangeToken, out int single))
        {
            start = ValidateRange(single, min, max, name, rangeToken);
            end = max;
        }
        else
        {
            throw new CronParseException($"Invalid step start '{rangeToken}' for {name}.", name, rangeToken);
        }

        if (!TryParseNumber(stepToken, out int step) || step <= 0)
            throw new CronParseException($"Invalid step value '{stepToken}' for {name}.", name, stepToken);

        for (int value = start; value <= end; value += step)
            acc.Values.Add(value);
    }

    private static void ParseRange(string token, CronFieldKind kind, string name, Accumulator acc)
    {
        var parts = token.Split('-', 2);
        if (parts.Length != 2 || !TryParseNumber(parts[0], out int start) || !TryParseNumber(parts[1], out int end))
            throw new CronParseException($"Invalid range '{token}' for {name}.", name, token);

        int min = FieldMin[(int)kind];
        int max = FieldMax[(int)kind];

        start = ValidateRange(start, min, max, name, token);
        end = ValidateRange(end, min, max, name, token);

        if (start > end)
            throw new CronParseException($"Range {start}-{end} invalid for {name}.", name, token);

        for (int value = start; value <= end; value++)
            acc.Values.Add(value);
    }

    // -------------------------------------------------------
    // Day specials
    // -------------------------------------------------------

    /// <summary>
    /// Parses the Quartz-style day specials. Day of month supports "L", "L-n", "LW" and "dW";
    /// day of week supports "dL" (last such weekday of the month). Returns false when the token
    /// is not a special, and throws when it looks like one but is malformed.
    /// </summary>
#pragma warning disable S3776
    private static bool TryParseSpecial(string token, CronFieldKind kind, string name, Accumulator acc)
#pragma warning restore S3776
    {
        if (kind == CronFieldKind.DayOfMonth)
        {
            if (token == "L")
            {
                acc.LastDay = true;
                return true;
            }

            if (token == "LW")
            {
                acc.LastWeekday = true;
                return true;
            }

            if (token.StartsWith("L-", StringComparison.Ordinal))
            {
                string offsetToken = token[2..];
                if (!TryParseNumber(offsetToken, out int offset))
                    throw new CronParseException($"Invalid last-day offset '{offsetToken}' for {name}.", name, token);

                if (offset > MaxLastDayOffset)
                    throw new CronParseException(
                        $"Last-day offset must be between 0 and {MaxLastDayOffset} for {name}.", name, token);

                // "L-0" is the same day as "L".
                if (offset == 0) acc.LastDay = true;
                else acc.LastDayOffsets.Add(offset);

                return true;
            }

            if (token.EndsWith('W'))
            {
                string dayToken = token[..^1];
                if (!TryParseNumber(dayToken, out int day))
                    throw new CronParseException(
                        $"The 'W' special needs a single day of month, for example '15W', but got '{token}'.", name, token);

                acc.NearestWeekdays.Add(ValidateRange(day, 1, 31, name, token));
                return true;
            }

            return false;
        }

        if (kind == CronFieldKind.DayOfWeek && token.Length > 1 && token.EndsWith('L'))
        {
            string dayToken = token[..^1];
            if (!TryParseNumber(dayToken, out int day))
                throw new CronParseException(
                    $"Invalid last-day-of-week value '{dayToken}' for {name}.", name, token);

            acc.LastWeekdaysOfMonth.Add(ValidateRange(day, 0, 7, name, token));
            return true;
        }

        return false;
    }

    private static bool ContainsSpecial(string token) => token.Contains('L') || token.Contains('W');

    // -------------------------------------------------------
    // Values
    // -------------------------------------------------------

    private static CronField Build(string token, string name, CronFieldKind kind, Accumulator acc)
    {
        // Quartz forbids mixing "W" with "L" — the two have no defined precedence.
        if (acc.NearestWeekdays.Count > 0 && acc.HasLastDaySpecial)
            throw new CronParseException(
                $"The 'W' special cannot be combined with 'L' in the {name} field.", name, token);

        if (acc.Values.Count == 0 && !acc.HasSpecials)
            throw new CronParseException($"Cron field '{token}' for {name} produced no values.", name, token);

        return new CronField
        {
            Name = name,
            Token = token,
            Values = Normalize([.. acc.Values], kind),
            LastDay = acc.LastDay,
            LastDayOffsets = Normalize([.. acc.LastDayOffsets], kind),
            LastWeekday = acc.LastWeekday,
            NearestWeekdays = Normalize([.. acc.NearestWeekdays], kind),
            LastWeekdaysOfMonth = Normalize([.. acc.LastWeekdaysOfMonth], kind),
        };
    }

    /// <summary>
    /// Folds day-of-week 7 into 0 (both are Sunday), then sorts and de-duplicates.
    /// </summary>
    private static int[] Normalize(int[] values, CronFieldKind kind)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (kind == CronFieldKind.DayOfWeek)
                values[i] = values[i] == 7 ? 0 : values[i];
        }

        Array.Sort(values);

        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (i == 0 || values[i] != values[i - 1])
                values[count++] = values[i];
        }

        return count == values.Length ? values : values[..count];
    }

    private static bool TryParseValue(string part, CronFieldKind kind, string name, out int value)
    {
        value = 0;

        if (!TryParseNumber(part, out int parsed))
            return false;

        value = ValidateRange(parsed, FieldMin[(int)kind], FieldMax[(int)kind], name, part);
        return true;
    }

    /// <summary>
    /// Parses a non-negative integer with no sign, whitespace or culture-specific digits.
    /// </summary>
    private static bool TryParseNumber(ReadOnlySpan<char> token, out int value) =>
        int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static int ValidateRange(int value, int min, int max, string name, string token)
    {
        if (value < min || value > max)
            throw new CronParseException($"Value {value} out of range [{min}-{max}] for {name}.", name, token);

        return value;
    }

    /// <summary>
    /// Collects the pieces of a field while it is being parsed.
    /// </summary>
    private sealed class Accumulator
    {
        public List<int> Values { get; } = [];
        public List<int> LastDayOffsets { get; } = [];
        public List<int> NearestWeekdays { get; } = [];
        public List<int> LastWeekdaysOfMonth { get; } = [];
        public bool LastDay { get; set; }
        public bool LastWeekday { get; set; }

        public bool HasLastDaySpecial => LastDay || LastWeekday || LastDayOffsets.Count > 0;

        public bool HasSpecials => HasLastDaySpecial || NearestWeekdays.Count > 0 || LastWeekdaysOfMonth.Count > 0;
    }
}
