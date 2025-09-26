using System.Diagnostics;
using System.Globalization;

namespace Sa.Media.FFmpeg.Services;

internal static class StrExtensions
{
    [DebuggerStepThrough]
    public static int? StrToInt(this string? str) => int.TryParse(str, CultureInfo.InvariantCulture, out int result) ? result : null;
    [DebuggerStepThrough]
    public static double? StrToDouble(this string? str) => double.TryParse(str, CultureInfo.InvariantCulture, out double result) ? result : null;

}
