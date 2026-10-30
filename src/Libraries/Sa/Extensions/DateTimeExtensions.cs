using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sa.Extensions;

internal static class DateTimeExtensions
{
    /// <summary>
    /// Unix timestamp. Skips <see cref="DateTime.ToUniversalTime"/> when the value is already UTC.
    /// </summary>
    /// <remarks>
    /// The stamp is floored, not truncated: the cast of <see cref="TimeSpan.TotalSeconds"/> /
    /// <see cref="TimeSpan.TotalMilliseconds"/> rounds towards zero, which for any pre-1970
    /// date with a sub-second component rounded the value <em>up</em> towards the epoch
    /// (…:59.5 became <c>0</c>, the epoch itself). After 1970 the value is non-negative and
    /// flooring is a no-op.
    /// </remarks>
    [DebuggerStepThrough,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToUnixTimestamp(this DateTime dateTime, bool isInMilliseconds = false)
    {
        var dt = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
        var ts = dt.Subtract(DateTime.UnixEpoch);
        return isInMilliseconds
            ? (long)Math.Floor(ts.TotalMilliseconds)
            : (long)Math.Floor(ts.TotalSeconds);
    }

    [DebuggerStepThrough,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset StartOfDay(this DateTimeOffset dateTime) => new(dateTime.Year, dateTime.Month, dateTime.Day, 0, 0, 0, 0, dateTime.Offset);
    [DebuggerStepThrough,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset EndOfDay(this DateTimeOffset dateTime) => dateTime.StartOfDay().AddDays(1);
    [DebuggerStepThrough,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset StartOfMonth(this DateTimeOffset dateTime) => new(dateTime.Year, dateTime.Month, 1, 0, 0, 0, 0, dateTime.Offset);
    [DebuggerStepThrough,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset EndOfMonth(this DateTimeOffset dateTime) => dateTime.StartOfMonth().AddMonths(1);
    [DebuggerStepThrough,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset StartOfYear(this DateTimeOffset dateTime) => new(dateTime.Year, 1, 1, 0, 0, 0, 0, dateTime.Offset);
    [DebuggerStepThrough,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset EndOfYear(this DateTimeOffset dateTime) => dateTime.StartOfYear().AddYears(1);
}
