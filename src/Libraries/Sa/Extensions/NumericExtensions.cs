using System.Diagnostics;

namespace Sa.Extensions;

internal static class NumericExtensions
{
    private static readonly DateTime UnixEpoch = DateTime.UnixEpoch;
    private static readonly double MaxUnixSeconds = (DateTime.MaxValue - UnixEpoch).TotalSeconds;

    [DebuggerStepThrough]
    public static DateTime ToDateTimeFromUnixTimestamp(this uint timestamp)
        => (timestamp > MaxUnixSeconds
            ? UnixEpoch.AddMilliseconds(timestamp)
            : UnixEpoch.AddSeconds(timestamp)).ToUniversalTime();


    [DebuggerStepThrough]
    public static DateTime? ToDateTimeFromUnixTimestamp(this string timestampString)
        => long.TryParse(timestampString, out var result) ? result.ToDateTimeFromUnixTimestamp() : null;

    [DebuggerStepThrough]
    public static DateTime ToDateTimeFromUnixTimestamp(this long timestamp)
        => (timestamp > MaxUnixSeconds
            ? UnixEpoch.AddMilliseconds(timestamp)
            : UnixEpoch.AddSeconds(timestamp)).ToUniversalTime();

    [DebuggerStepThrough]
    public static DateTime ToDateTimeFromUnixTimestamp(this ulong timestamp)
    {
        // The unchecked (long) cast wrapped past long.MaxValue into a negative number, so
        // ulong.MaxValue landed before 1970 — a plausible-looking date instead of a rejected
        // input. The rest of the family reports out-of-range values by letting
        // AddSeconds/AddMilliseconds throw ArgumentOutOfRangeException; this does the same.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timestamp, (ulong)long.MaxValue);
        return ToDateTimeFromUnixTimestamp((long)timestamp);
    }

    [DebuggerStepThrough]
    public static DateTime ToDateTimeFromUnixTimestamp(this double timestamp)
    {
        // The double cast truncates towards zero, so -0.5 s became 0 and the instant
        // 1969-12-31T23:59:59.5Z came back as the epoch. Flooring is the convention for
        // timestamp conversion and keeps the result at or before the true instant. NaN and
        // ±∞ used to reach the cast as long.MinValue (an implementation-defined result) and
        // then fail inside AddSeconds, so they are rejected here.
        if (!double.IsFinite(timestamp))
            throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp, "Timestamp must be a finite number.");

        return ToDateTimeFromUnixTimestamp((long)Math.Floor(timestamp));
    }

    [DebuggerStepThrough]
    public static DateTime? ToDateTimeFromUnixTimestamp(this long? ts)
        => ts.HasValue ? ts.Value.ToDateTimeFromUnixTimestamp() : null;

    [DebuggerStepThrough]
    public static DateTime? ToDateTimeFromUnixTimestamp(this ulong? ts)
        => ts.HasValue ? ts.Value.ToDateTimeFromUnixTimestamp() : null;

    [DebuggerStepThrough]
    public static DateTime? ToDateTimeFromUnixTimestamp(this double? ts)
        => ts.HasValue ? ts.Value.ToDateTimeFromUnixTimestamp() : null;

    [DebuggerStepThrough]
    public static DateTimeOffset ToDateTimeOffsetFromUnixTimestamp(this long timestamp)
        => ToDateTimeFromUnixTimestamp(timestamp);
}
