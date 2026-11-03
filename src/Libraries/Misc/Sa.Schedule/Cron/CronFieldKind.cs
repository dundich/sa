namespace Sa.Schedule.Cron;

/// <summary>
/// Identifies a positional field of a cron expression: 5 fields, or 6 when the expression
/// starts with an optional seconds field.
/// </summary>
internal enum CronFieldKind
{
    /// <summary>Field 0 of a 6-field expression — second (0-59). Absent in 5-field expressions.</summary>
    Second,

    /// <summary>Minute (0-59).</summary>
    Minute,

    /// <summary>Hour (0-23).</summary>
    Hour,

    /// <summary>Day of month (1-31), the only field that supports the L/W specials.</summary>
    DayOfMonth,

    /// <summary>Month (1-12).</summary>
    Month,

    /// <summary>Day of week (0-6, where 7 is also Sunday), supports the "dL" special.</summary>
    DayOfWeek,
}
