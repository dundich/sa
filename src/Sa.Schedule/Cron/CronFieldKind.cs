namespace Sa.Schedule.Cron;

/// <summary>
/// Identifies a positional field of a 5-field cron expression.
/// </summary>
internal enum CronFieldKind
{
    /// <summary>Field 1 — minute (0-59).</summary>
    Minute,

    /// <summary>Field 2 — hour (0-23).</summary>
    Hour,

    /// <summary>Field 3 — day of month (1-31), the only field that supports the L/W specials.</summary>
    DayOfMonth,

    /// <summary>Field 4 — month (1-12).</summary>
    Month,

    /// <summary>Field 5 — day of week (0-6, where 7 is also Sunday), supports the "dL" special.</summary>
    DayOfWeek,
}
