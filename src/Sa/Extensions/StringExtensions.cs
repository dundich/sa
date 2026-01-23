using System.Diagnostics;
using System.Text;
using Sa.Classes;

namespace Sa.Extensions;

internal static partial class StringExtensions
{
    [DebuggerStepThrough]
    public static string? NullIfEmpty(this string? str)
        => string.IsNullOrWhiteSpace(str) ? default : str;


    [DebuggerStepThrough]
    public static uint GetMurmurHash3(this string str, uint seed = 0)
        => MurmurHash3.Hash32(Encoding.UTF8.GetBytes(str), seed);


    [DebuggerStepThrough]
    public static string NormalizeWhiteSpace(this string? str, bool isTrimmed = true)
    {
        if (string.IsNullOrEmpty(str)) return String.Empty;

        if (isTrimmed)
        {
            str = str.Trim();
            if (str.Length == 0)
                return string.Empty;
        }

        var sb = new StringBuilder(str.Length);
        bool previousWasWhiteSpace = false;

        foreach (char c in str)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasWhiteSpace)
                {
                    sb.Append(' ');
                    previousWasWhiteSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                previousWasWhiteSpace = false;
            }
        }

        string result = sb.ToString();
        return isTrimmed ? result.Trim() : result;
    }
}
