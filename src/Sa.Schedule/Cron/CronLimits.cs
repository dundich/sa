namespace Sa.Schedule.Cron;

/// <summary>
/// The numeric bounds of each of the five fields — a single source of truth shared by
/// <see cref="CronFieldParser"/> (which validates against them) and <see cref="CronTiming"/>
/// (which sizes its lookup tables from them), so the two cannot drift apart.
/// </summary>
internal static class CronLimits
{
    public const int MinuteMin = 0;
    public const int MinuteMax = 59;

    public const int HourMin = 0;
    public const int HourMax = 23;

    public const int DayOfMonthMin = 1;
    public const int DayOfMonthMax = 31;

    public const int MonthMin = 1;
    public const int MonthMax = 12;

    /// <summary>Day of week as written in the expression: 7 is accepted and means Sunday.</summary>
    public const int DayOfWeekMin = 0;
    public const int DayOfWeekMax = 7;

    /// <summary>Distinct weekday values once 7 is folded into 0 (Sunday..Saturday = 0..6).</summary>
    public const int DayOfWeekCount = 7;

    /// <summary>Maximum accepted "L-n" offset; a month never has more than 31 days.</summary>
    public const int MaxLastDayOffset = DayOfMonthMax - 1;
}
