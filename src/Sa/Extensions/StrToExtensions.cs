using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sa.Extensions;

internal static class StrToExtensions
{

    [DebuggerStepThrough]
    public static bool? StrToBool(this string? str) => bool.TryParse(str, out bool result) ? result : null;

    [DebuggerStepThrough]
    public static bool? StrToBool(this ReadOnlySpan<char> str) => bool.TryParse(str, out bool result) ? result : null;


    [DebuggerStepThrough]
    public static int? StrToInt(this string? str) => int.TryParse(str, CultureInfo.InvariantCulture, out int result) ? result : null;
    [DebuggerStepThrough]
    public static int? StrToInt(this ReadOnlySpan<char> str) => int.TryParse(str, CultureInfo.InvariantCulture, out int result) ? result : null;


    [DebuggerStepThrough]
    public static short? StrToShort(this string? str) => short.TryParse(str, CultureInfo.InvariantCulture, out short result) ? result : null;
    [DebuggerStepThrough]
    public static short? StrToShort(this ReadOnlySpan<char> str) => short.TryParse(str, CultureInfo.InvariantCulture, out short result) ? result : null;


    [DebuggerStepThrough]
    public static ushort? StrToUShort(this string? str) => ushort.TryParse(str, CultureInfo.InvariantCulture, out ushort result) ? result : null;

    [DebuggerStepThrough]
    public static ushort? StrToUShort(this ReadOnlySpan<char> str) => ushort.TryParse(str, CultureInfo.InvariantCulture, out ushort result) ? result : null;


    [DebuggerStepThrough]
    public static long? StrToLong(this string? str) => long.TryParse(str, CultureInfo.InvariantCulture, out long result) ? result : null;
    [DebuggerStepThrough]
    public static long? StrToLong(this ReadOnlySpan<char> str) => long.TryParse(str, CultureInfo.InvariantCulture, out long result) ? result : null;


    [DebuggerStepThrough]
    public static ulong? StrToULong(this string? str) => ulong.TryParse(str, CultureInfo.InvariantCulture, out ulong result) ? result : null;

    [DebuggerStepThrough]
    public static ulong? StrToULong(this ReadOnlySpan<char> str) => ulong.TryParse(str, CultureInfo.InvariantCulture, out ulong result) ? result : null;


    [DebuggerStepThrough]
    public static byte[] StrToBytes(this string str, Encoding? encoding = null) => (encoding ?? Encoding.UTF8).GetBytes(str);


    [DebuggerStepThrough]
    public static Guid? StrToGuid(this string? str) => Guid.TryParse(str, CultureInfo.InvariantCulture, out Guid result) ? result : null;
    [DebuggerStepThrough]
    public static Guid? StrToGuid(this ReadOnlySpan<char> str) => Guid.TryParse(str, CultureInfo.InvariantCulture, out Guid result) ? result : null;


    [DebuggerStepThrough]
    public static T StrToEnum<T>(this string? str, T defaultValue) where T : struct => (Enum.TryParse<T>(str, true, out T result)) ? result : defaultValue;


    [DebuggerStepThrough]
    public static DateTime? StrToDate(this string? str, IFormatProvider? provider = null, DateTimeStyles style = DateTimeStyles.None)
       => DateTime.TryParseExact(str, DateFmt.Formats, provider ?? CultureInfo.InvariantCulture, style, out DateTime result)
            ? result
            : null;

    [DebuggerStepThrough]
    public static DateTime? StrToDate(this ReadOnlySpan<char> str, IFormatProvider? provider = null, DateTimeStyles style = DateTimeStyles.None)
        => DateTime.TryParseExact(str, DateFmt.Formats, provider ?? CultureInfo.InvariantCulture, style, out DateTime result)
            ? result
            : null;

}

#region Date Fmts
static class DateFmt
{
    public static readonly string[] Formats =
    [
        "yyyyMMdd",
        "dd.MM.yyyy",
        "dd-MM-yyyy",
        "yyyy-MM-dd",
        "MM/dd/yyyy HH:mm:ss",
        "MM/dd/yyyy",
        "dd.MM.yy",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "dd.MM.yyyy HH:mm",
        "dd-MM-yyyy HH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "dd.MM.yyyy HH:mm:ss",
        "yyyy-MM-ddK",
        "yyyyMMddK",
        "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "yyyyMMddTHH:mm:ss.fffffffK",
        "yyyy-MM-ddTHH:mm:ss,fffffffK",
        "yyyyMMddTHH:mm:ss,fffffffK",
        "yyyy-MM-ddTHH:mm:ss.ffffffK",
        "yyyyMMddTHH:mm:ss.ffffffK",
        "yyyy-MM-ddTHH:mm:ss,ffffffK",
        "yyyyMMddTHH:mm:ss,ffffffK",
        "yyyy-MM-ddTHH:mm:ss.fffffK",
        "yyyyMMddTHH:mm:ss.fffffK",
        "yyyy-MM-ddTHH:mm:ss,fffffK",
        "yyyyMMddTHH:mm:ss,fffffK",
        "yyyy-MM-ddTHH:mm:ss.ffffK",
        "yyyyMMddTHH:mm:ss.ffffK",
        "yyyy-MM-ddTHH:mm:ss,ffffK",
        "yyyyMMddTHH:mm:ss,ffffK",
        "yyyy-MM-ddTHH:mm:ss.fffK",
        "yyyyMMddTHH:mm:ss.fffK",
        "yyyy-MM-ddTHH:mm:ss.ffK",
        "yyyyMMddTHH:mm:ss.ffK",
        "yyyy-MM-ddTHH:mm:ss,ffK",
        "yyyyMMddTHH:mm:ss,ffK",
        "yyyy-MM-ddTHH:mm:ss.fK",
        "yyyyMMddTHH:mm:ss.fK",
        "yyyy-MM-ddTHH:mm:ss,fK",
        "yyyyMMddTHH:mm:ss,fK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyyMMddTHH:mm:ssK",
        "yyyy-MM-ddTHHmmss.fffffffK",
        "yyyyMMddTHHmmss.fffffffK",
        "yyyy-MM-ddTHHmmss,fffffffK",
        "yyyyMMddTHHmmss,fffffffK",
        "yyyy-MM-ddTHHmmss.ffffffK",
        "yyyyMMddTHHmmss.ffffffK",
        "yyyy-MM-ddTHHmmss,ffffffK",
        "yyyyMMddTHHmmss,ffffffK",
        "yyyy-MM-ddTHHmmss.fffffK",
        "yyyyMMddTHHmmss.fffffK",
        "yyyy-MM-ddTHHmmss,fffffK",
        "yyyyMMddTHHmmss,fffffK",
        "yyyy-MM-ddTHHmmss.ffffK",
        "yyyyMMddTHHmmss.ffffK",
        "yyyy-MM-ddTHHmmss,ffffK",
        "yyyyMMddTHHmmss,ffffK",
        "yyyy-MM-ddTHHmmss.ffK",
        "yyyyMMddTHHmmss.ffK",
        "yyyy-MM-ddTHHmmss,ffK",
        "yyyyMMddTHHmmss,ffK",
        "yyyy-MM-ddTHHmmss.fK",
        "yyyyMMddTHHmmss.fK",
        "yyyy-MM-ddTHHmmss,fK",
        "yyyyMMddTHHmmss,fK",
        "yyyy-MM-ddTHHmmssK",
        "yyyyMMddTHHmmssK",
        "yyyy-MM-ddTHH:mmK",
        "yyyyMMddTHH:mmK",
        "yyyy-MM-ddTHHK",
        "yyyyMMddTHHK",
        "o"
    ];
}
#endregion