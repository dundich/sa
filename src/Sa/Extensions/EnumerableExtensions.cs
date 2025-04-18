using System.Diagnostics;

namespace Sa.Extensions;


public static class EnumerableExtensions
{
    [DebuggerStepThrough]
    public static string JoinByString<T>(this IEnumerable<T> source, string? joinWith = null)
    {
        if (source == null) return default!;
        return string.Join(joinWith, source);
    }

    [DebuggerStepThrough]
    public static string JoinByString<T>(this IEnumerable<T> source, Func<T, T> map, string? joinWith = null)
    {
        if (source == null) return default!;
        return string.Join(joinWith, source.Select(map));
    }

    [DebuggerStepThrough]
    public static string JoinByString<T>(this IEnumerable<T> source, Func<T, int, T> map, string? joinWith = null)
    {
        if (source == null) return default!;
        return string.Join(joinWith, source.Select(map));
    }
}

