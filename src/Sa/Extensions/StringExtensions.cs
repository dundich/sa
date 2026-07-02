using Sa.Classes;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Sa.Extensions;

internal static partial class StringExtensions
{
    /// <summary>
    /// Returns <paramref name="str"/> unless it is null, empty, or consists entirely of whitespace — in which cases returns <c>null</c>.
    /// </summary>
    [DebuggerStepThrough, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? NullIfEmpty(this string? str)
    {
        if (string.IsNullOrEmpty(str)) return null;

        // Single-pass whitespace check — avoids a separate IsOnlyWhitespace call
#pragma warning disable S3267
        foreach (char c in str)
        {
            if (!char.IsWhiteSpace(c)) return str;
        }
#pragma warning restore S3267
        return null;
    }

    /// <summary>
    /// Replaces all consecutive white-space characters with a single space. Zero-allocation variant available via <see cref="NormalizeWhiteSpaceSpan"/>.
    /// </summary>
    [DebuggerStepThrough]
#pragma warning disable S3776
    public static string NormalizeWhiteSpace(this string? str, bool isTrimmed = true)
#pragma warning restore S3776
    {
        if (string.IsNullOrEmpty(str)) return String.Empty;

        bool slowPath = false;

        int len = str.Length;
        for (int i = 0; i < len; i++)
        {
            char c = str[i];
            if (char.IsWhiteSpace(c) || char.IsSeparator(c) || char.IsControl(c))
            {
                slowPath = true;
                break;
            }
        }

        if (!slowPath)
        {
            // No whitespace found — just trim and return
            return isTrimmed ? str.Trim() : str;
        }

        // Slow path: span-based normalization — allocates one new string, avoids StringBuilder heap churn
        Span<char> dest = stackalloc char[len];
        int w = 0;
        bool prevWhite = false;
        for (int i = 0; i < len; i++)
        {
            char c = str[i];
            if (char.IsWhiteSpace(c) || char.IsSeparator(c) || char.IsControl(c))
            {
                if (!prevWhite)
                {
                    dest[w++] = ' ';
                    prevWhite = true;
                }
            }
            else
            {
                dest[w++] = c;
                prevWhite = false;
            }
        }

        string result = dest[..w].ToString();
        return isTrimmed ? result.Trim() : result;
    }

    /// <summary>
    /// Span-based zero-allocation normalization that writes into <paramref name="dest"/>.
    /// Returns the number of characters written.
    /// </summary>
    [DebuggerStepThrough]
    public static int NormalizeWhiteSpaceSpan(ReadOnlySpan<char> str, Span<char> dest, bool isTrimmed = true)
    {
        if (str.IsEmpty) return 0;

        if (isTrimmed)
        {
            str = str.Trim();
            if (str.IsEmpty) return 0;
        }

        int len = str.Length;
        int w = 0;
        bool prevWhite = false;
        for (int i = 0; i < len; i++)
        {
            char c = str[i];
            if (char.IsWhiteSpace(c) || char.IsSeparator(c) || char.IsControl(c))
            {
                if (!prevWhite)
                {
                    dest[w++] = ' ';
                    prevWhite = true;
                }
            }
            else
            {
                dest[w++] = c;
                prevWhite = false;
            }
        }

        if (isTrimmed && w > 0 && dest[w - 1] == ' ')
            w--;

        return w;
    }

    /// <summary>
    /// Computes MurmurHash3 for the UTF-8 encoding of <paramref name="str"/> without allocating a byte array.
    /// </summary>
    [DebuggerStepThrough]
    public static uint GetMurmurHash3(this string str, uint seed = 0)
    {
        // Estimate UTF-8 byte length (upper bound: 3 bytes per char for BMP Latin, up to 4 for emoji)
        int estimatedLen = str.Length * 3;
        Span<byte> buf = estimatedLen <= 512 ? stackalloc byte[estimatedLen] : new byte[estimatedLen];

        int actualLen = Encoding.UTF8.GetBytes(str, buf);
        return MurmurHash3.Hash32(buf[..actualLen], seed);
    }
}
