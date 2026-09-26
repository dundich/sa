using System.Collections.Concurrent;

namespace Sa.Outbox.PostgreSql.Commands;

/// <summary>
/// Splits a large logical batch into SQL-friendly chunks and caches generated SQL strings.
///
/// PostgreSQL limits the number of parameters in a single statement to 65 535.
/// When building bulk UPDATE/INSERT statements that cover many rows, the parameter
/// count can easily exceed this limit. This class solves two problems at once:
///
/// 1. **Batch splitting** — cuts the input into chunks that fit within a safe
///    parameter budget (≤ 512 elements per chunk). Elements are aligned to multiples
///    of 16 because each element typically contributes ~16 SQL parameters.
///
/// 2. **SQL caching** — stores generated SQL templates keyed by chunk size so that
///    identical sizes reuse the same string instance instead of allocating a new one.
///    The cache is bounded: with maxLen = 512 there are at most 47 unique keys
///    (multiples of 16 up to 512, plus remainders 1–15), which consumes roughly
///    50 KB even for long SQL templates — negligible and never unbounded.
///    The cache is a <see cref="ConcurrentDictionary{TKey,TValue}"/>, so an instance is
///    thread-safe and may be shared between threads; under contention <c>genSql</c>
///    may run twice for the same key, in which case one of the results is discarded.
/// </summary>
internal sealed class SqlCacheSplitter(Func<int, string> genSql)
{
    private readonly ConcurrentDictionary<int, string> _sqlCache = [];

    /// <summary>
    /// Returns an enumeration of (sql, length) pairs that together cover the requested
    /// number of elements. Each pair represents one database round-trip: the caller
    /// slices the input collection and executes the SQL with the corresponding slice.
    /// </summary>
    /// <param name="len">Total number of elements to cover. Must be positive.</param>
    /// <param name="maxLen">Maximum chunk size in elements. Defaults to 512.</param>
    /// <returns>
    /// Sequence of tuples where each element is <c>(sqlTemplate, count)</c>.
    /// All lengths are positive; every length except possibly the last is a multiple of 16.
    /// The sum of all lengths equals <paramref name="len"/>.
    /// </returns>
    public IEnumerable<(string sql, int length)> GetSql(int len, int maxLen = 512)
    {
        if (len <= 0)
        {
            yield break;
        }

        int multipleOf16 = len / 16 * 16;

        if (multipleOf16 > maxLen)
        {
            int multipleOfMax = multipleOf16 / maxLen;

            for (int i = 0; i < multipleOfMax; i++)
            {
                yield return GetOrAdd(maxLen);
            }

            int rest = multipleOf16 - multipleOfMax * maxLen;

            if (rest > 0)
            {
                yield return GetOrAdd(rest);
            }
        }
        else if (multipleOf16 > 0)
        {
            yield return GetOrAdd(multipleOf16);
        }

        int diff = len - multipleOf16;

        if (diff > 0)
        {
            yield return GetOrAdd(diff);
        }
    }

    /// <summary>
    /// Retrieves a cached SQL template for the given length or generates and caches a new one.
    /// </summary>
    private (string, int) GetOrAdd(int len)
    {
        string sql = _sqlCache.GetOrAdd(len, k => genSql(k));

        return (sql, len);
    }
}
