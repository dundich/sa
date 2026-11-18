using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Sa.Data.S3.Utils;


internal static class StringUtils
{
    [ThreadStatic]
    private static StringBuilder? _sharedBuilder;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringBuilder Append(this StringBuilder builder, string start, int middle, string end)
    {
        return builder
            .Append(start)
            .Append(middle)
            .Append(end);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringBuilder Append(this StringBuilder builder, string start, string middle, string end)
    {
        return builder
            .Append(start)
            .Append(middle)
            .Append(end);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringBuilder GetBuilder()
    {
        return Interlocked.Exchange(ref _sharedBuilder, null)
               ?? new StringBuilder(4096);
    }

    /// <summary>
    /// Appends <paramref name="value"/> with XML special characters escaped. S3 returns ETags and
    /// object keys verbatim, and both go into request bodies (CompleteMultipartUpload,
    /// DeleteObjects) — an unescaped <c>&amp;</c> or <c>&lt;</c> would produce invalid XML.
    /// </summary>
    public static void AppendXmlEscaped(StringBuilder builder, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                default: builder.Append(ch); break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Format(ref Span<char> buffer, DateTimeOffset dateTime, string format)
    {
        return dateTime.TryFormat(buffer, out var written, format, CultureInfo.InvariantCulture)
            ? written
            : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FormatX2(ref Span<char> buffer, byte value)
    {
        return value.TryFormat(buffer, out var written, "x2", CultureInfo.InvariantCulture)
            ? written
            : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FormatX2(ref Span<char> buffer, char value)
    {
        return ((int)value).TryFormat(buffer, out var written, "x2", CultureInfo.InvariantCulture)
            ? written
            : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Flush(this StringBuilder builder)
    {
        var result = builder.ToString();
        builder.Clear();

        Interlocked.Exchange(ref _sharedBuilder, builder);
        return result;
    }
}
