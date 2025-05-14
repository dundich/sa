using Sa.Classes;
using System.Diagnostics;

namespace Sa.Extensions;

internal static class StringExtensions
{
    [DebuggerStepThrough]
    public static string? NullIfEmpty(this string? str) => string.IsNullOrWhiteSpace(str) ? default : str;


    [DebuggerStepThrough]
    public static uint GetMurmurHash3(this string str, uint seed = 0) => MurmurHash3.Hash32(str.StrToBytes(), seed);
}
