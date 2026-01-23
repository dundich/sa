using System.Diagnostics;

namespace Sa.Extensions;

internal static class DateTimeExtensions
{
    /// <summary>
    /// Unix timestamp
    /// </summary>
    [DebuggerStepThrough]
    public static long ToUnixTimestamp(this DateTime dateTime, bool isInMilliseconds = false)
    {
        TimeSpan ts = dateTime.ToUniversalTime().Subtract(DateTime.UnixEpoch);
        return isInMilliseconds ? (long)ts.TotalMilliseconds : (long)ts.TotalSeconds;
    }

    [DebuggerStepThrough]
    public static DateTimeOffset StartOfDay(this DateTimeOffset dateTime) => new(dateTime.Year, dateTime.Month, dateTime.Day, 0, 0, 0, 0, dateTime.Offset);
    [DebuggerStepThrough]
    public static DateTimeOffset EndOfDay(this DateTimeOffset dateTime) => dateTime.StartOfDay().AddDays(1);
    [DebuggerStepThrough]
    public static DateTimeOffset StartOfMonth(this DateTimeOffset dateTime) => new(dateTime.Year, dateTime.Month, 1, 0, 0, 0, 0, dateTime.Offset);
    [DebuggerStepThrough]
    public static DateTimeOffset EndOfMonth(this DateTimeOffset dateTime) => dateTime.StartOfMonth().AddMonths(1);
    [DebuggerStepThrough]
    public static DateTimeOffset StartOfYear(this DateTimeOffset dateTime) => new(dateTime.Year, 1, 1, 0, 0, 0, 0, dateTime.Offset);
    [DebuggerStepThrough]
    public static DateTimeOffset EndOfYear(this DateTimeOffset dateTime) => dateTime.StartOfYear().AddYears(1);
}
