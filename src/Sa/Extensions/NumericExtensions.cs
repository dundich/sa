using System.Diagnostics;

namespace Sa.Extensions;

public static class NumericExtensions
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
        => ToDateTimeFromUnixTimestamp((long)timestamp);

    [DebuggerStepThrough]
    public static DateTime ToDateTimeFromUnixTimestamp(this double timestamp)
        => ToDateTimeFromUnixTimestamp((long)timestamp);

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
