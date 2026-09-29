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
///    parameter budget. The chunk ceiling is <see cref="DefaultMaxLen"/> elements and is
///    sized from the *measured* per-row parameter ratio: 8 indexed (plus 4 reused
///    common) for SqlFinishDelivery and 4 for SqlError. The earlier "~16 params per
///    element" estimate was wrong by a factor of 2–4; the real numbers are pinned in
///    SqlOutboxBuilderTests.BulkStatements_ParameterPerRowRatio_Is8And4_Not16.
///    At the current ceiling (1024) the finish statement binds 8×1024+4 = 8196
///    parameters — 12.5% of the PostgreSQL limit, an 8× margin below the ~8190-row
///    ceiling one statement can actually carry.
///
/// 2. **SQL caching** — stores generated SQL templates keyed by chunk size so that
///    identical sizes reuse the same string instance instead of allocating a new one.
///    The cache is bounded: at most 79 unique keys (64 multiples of 16 up to 1024,
///    plus remainders 1–15). Worst case, with every key generated, that is a few MB
///    of SQL text; a real consumer exercises only 2–4 distinct sizes per splitter,
///    so in practice it is a handful of strings. Never unbounded.
///    The cache is a <see cref="ConcurrentDictionary{TKey,TValue}"/>, so an instance is
///    thread-safe and may be shared between threads; under contention <c>genSql</c>
///    may run twice for the same key, in which case one of the results is discarded.
/// </summary>
internal sealed class SqlCacheSplitter(Func<int, string> genSql)
{
    /// <summary>
    /// Maximum number of rows a single SQL chunk may cover. Shared with
    /// <c>BatchParams.MaxIndex</c>: the parameter-name cache pre-allocates names for
    /// indexes below this value, and a chunk at or above it silently falls back to
    /// per-parameter string interpolation — so the two must never diverge, and the
    /// single constant makes that impossible by construction.
    /// 1024, not 512 (Q4, decided 2026-09-29): measured 8×512+4 = 4100 parameters for a
    /// 512-row finish chunk (6.3% of the 65 535 limit) left ~16× unused headroom and
    /// cost MaxBatchSize=1024 consumers two round trips per finish. At 1024 the finish
    /// statement uses 8196 parameters (12.5%) and the shipped 1024 preset completes in
    /// one round trip.
    /// </summary>
    public const int DefaultMaxLen = 1024;
    private readonly ConcurrentDictionary<int, string> _sqlCache = [];

    /// <summary>
    /// Returns an enumeration of (sql, length) pairs that together cover the requested
    /// number of elements. Each pair represents one database round-trip: the caller
    /// slices the input collection and executes the SQL with the corresponding slice.
    /// </summary>
    /// <param name="len">Total number of elements to cover. Must be positive.</param>
    /// <param name="maxLen">Maximum chunk size in elements. Defaults to <see cref="DefaultMaxLen"/>.</param>
    /// <returns>
    /// Sequence of tuples where each element is <c>(sqlTemplate, count)</c>.
    /// All lengths are positive; every length except possibly the last is a multiple of 16.
    /// The sum of all lengths equals <paramref name="len"/>.
    /// </returns>
    public IEnumerable<(string sql, int length)> GetSql(int len, int maxLen = DefaultMaxLen)
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
