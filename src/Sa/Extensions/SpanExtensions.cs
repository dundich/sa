using System.Diagnostics;

namespace Sa.Extensions;

internal static class SpanExtensions
{
    [DebuggerStepThrough]
    public static IEnumerable<Memory<T>> GetChunks<T>(this Memory<T> arr, int chunkSize)
    {
        for (int i = 0; i < arr.Length; i += chunkSize)
        {
            Memory<T> chunk = arr[i..Math.Min(i + chunkSize, arr.Length)];
            yield return chunk;
        }
    }

    /// <summary>
    /// Same as <see cref="GetChunks"/> but returns a materialized array with pre-allocated capacity.
    /// </summary>
    [DebuggerStepThrough]
    public static Memory<T>[] GetChunksArray<T>(this Memory<T> arr, int chunkSize)
    {
        int count = (arr.Length + chunkSize - 1) / chunkSize;
        var result = new Memory<T>[count];
        int idx = 0;
        for (int i = 0; i < arr.Length; i += chunkSize)
        {
            int len = Math.Min(chunkSize, arr.Length - i);
            result[idx++] = arr.Slice(i, len);
        }
        return result;
    }

    /// <summary>
    /// Combines Select and Where with indexes into a single call for optimal
    /// performance.
    /// <seealso href="https://github.com/jackmott/LinqFaster/blob/master/LinqFaster/SelectWhere.cs"/>
    /// </summary>
    /// <param name="source">The input sequence to filter and select</param>
    /// <param name="selector">The transformation with index to apply before filtering.</param>
    /// <param name="predicate">The predicate with index with which to filter result.</param>
    /// <returns>A sequence transformed and then filtered by selector and predicate with indexes.</returns>
    public static TResult[] SelectWhere<T, TResult>(this Span<T> source, Func<T, int, TResult> selector, Func<TResult, int, bool>? predicate = null)
    {
        TResult[] result = new TResult[source.Length];
        int idx = 0;
        for (int i = 0; i < source.Length; i++)
        {
            TResult? s = selector(source[i], i);
            if (predicate == null || predicate(s, i))
            {
                result[idx] = s;
                idx++;
            }
        }
        Array.Resize(ref result, idx);
        return result;
    }

    /// <summary>
    /// <seealso href="https://github.com/jackmott/LinqFaster/blob/master/LinqFaster/SelectWhere.cs"/>
    /// </summary>
    public static TResult[] SelectWhere<T, TResult>(this Span<T> source, Func<T, TResult> selector, Func<TResult, bool>? predicate = null)
    {
        TResult[] result = new TResult[source.Length];
        int idx = 0;
        for (int i = 0; i < source.Length; i++)
        {
            TResult? s = selector(source[i]);
            if (predicate == null || predicate(s))
            {
                result[idx] = s;
                idx++;
            }
        }
        Array.Resize(ref result, idx);
        return result;
    }


    public static TResult[] SelectWhere<T, TResult>(this ReadOnlySpan<T> source, Func<T, int, TResult> selector, Func<TResult, int, bool>? predicate = null)
    {
        TResult[] result = new TResult[source.Length];
        int idx = 0;
        for (int i = 0; i < source.Length; i++)
        {
            TResult? s = selector(source[i], i);
            if (predicate == null || predicate(s, i))
            {
                result[idx] = s;
                idx++;
            }
        }
        Array.Resize(ref result, idx);
        return result;
    }

    /// <summary>
    /// <seealso href="https://github.com/jackmott/LinqFaster/blob/master/LinqFaster/SelectWhere.cs"/>
    /// </summary>
    public static TResult[] SelectWhere<T, TResult>(this ReadOnlySpan<T> source, Func<T, TResult> selector, Func<TResult, bool>? predicate = null)
    {
        TResult[] result = new TResult[source.Length];
        int idx = 0;
        for (int i = 0; i < source.Length; i++)
        {
            TResult? s = selector(source[i]);
            if (predicate == null || predicate(s))
            {
                result[idx] = s;
                idx++;
            }
        }
        Array.Resize(ref result, idx);
        return result;
    }
}
