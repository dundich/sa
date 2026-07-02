using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sa.Extensions;

internal static class EnumerableExtensions
{
    [DebuggerStepThrough,MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string JoinByString<T>(this IEnumerable<T> source, string? joinWith = null)
    {
        if (source == null) return default!;
        return string.Join(joinWith, source);
    }

    /// <summary>
    /// Maps and joins elements with minimal allocations. Uses <see cref="ICollection{T}.Count"/> when available
    /// to pre-allocate, otherwise falls back to <c>string.Join</c>.
    /// </summary>
    [DebuggerStepThrough]
    public static string JoinByString<T>(this IEnumerable<T> source, Func<T, T> map, string? joinWith = null)
    {
        if (source == null) return default!;

        // Fast path: if source is also ICollection, use the count hint
        if (source is ICollection<T> coll)
        {
            var arr = new T[coll.Count];
            coll.CopyTo(arr, 0);
            int i = 0;
            foreach (var item in arr)
            {
                arr[i++] = map(item);
            }
            return string.Join(joinWith, arr);
        }

        return string.Join(joinWith, source.Select(map));
    }

    /// <summary>
    /// Maps with index and joins elements. Uses <see cref="ICollection{T}.Count"/> when available.
    /// </summary>
    [DebuggerStepThrough]
    public static string JoinByString<T>(this IEnumerable<T> source, Func<T, int, T> map, string? joinWith = null)
    {
        if (source == null) return default!;

        if (source is ICollection<T> coll)
        {
            var arr = new T[coll.Count];
            coll.CopyTo(arr, 0);
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = map(arr[i], i);
            }
            return string.Join(joinWith, arr);
        }

        return string.Join(joinWith, source.Select(map));
    }
}

