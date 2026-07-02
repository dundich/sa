using System.Diagnostics;
using System.Globalization;

namespace Sa.Media.FFmpeg.Services;

internal static class StrExtensions
{
    [DebuggerStepThrough]
    public static int? StrToInt(this ReadOnlySpan<char> str)
        => int.TryParse(str, CultureInfo.InvariantCulture, out var r) ? r : null;

    [DebuggerStepThrough]
    public static double? StrToDouble(this ReadOnlySpan<char> str)
        => double.TryParse(str, CultureInfo.InvariantCulture, out var r) ? r : null;
}
