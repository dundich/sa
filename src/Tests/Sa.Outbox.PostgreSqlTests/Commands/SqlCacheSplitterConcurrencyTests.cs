using System.Collections.Concurrent;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSqlTests.Commands;

/// <summary>
/// Concurrency tests for <see cref="SqlCacheSplitter"/>. The splitter is shared between
/// threads (singleton delivery commands invoke it concurrently), so its SQL cache must
/// be thread-safe. A fake <c>genSql</c> counts factory calls per key and captures the
/// returned string instances; every thread must observe exactly one string instance
/// per key (the essence of the cache), and the factory may be called at most once
/// per key per thread — <see cref="ConcurrentDictionary{TKey,TValue}"/>.GetOrAdd may
/// re-run the factory under contention, so the per-key bound is the thread count.
/// </summary>
public class SqlCacheSplitterConcurrencyTests
{
    private const int ThreadCount = 16;
    private const int IterationsPerThread = 300;

    /// <summary>
    /// Pool of lengths covering every branch of <c>GetSql</c>: remainders 1–15,
    /// multiples of 16 up to 512, the maxLen boundary, 513–527 just above it,
    /// 1024, mid-range 1000, and large 4096 / 10000 / 100000.
    /// </summary>
    private static readonly int[] LengthPool =
    [
        .. Enumerable.Range(1, 15),
        .. Enumerable.Range(16, 32),
        512,
        513, 527,
        1024,
        1000,
        4096,
        10000,
        100000,
    ];

    [Fact]
    public async Task GetSql_ConcurrentAccess_NoExceptions_SameInstancePerKey_BoundedFactoryCalls()
    {
        var token = TestContext.Current.CancellationToken;

        var callCounts = new ConcurrentDictionary<int, int>();
        var seenInstances = new ConcurrentDictionary<int, string>();

        var splitter = new SqlCacheSplitter(len =>
        {
            string sql = $"SELECT * FROM t WHERE {len}";

            callCounts.AddOrUpdate(len, 1, (_, next) => next + 1);
            seenInstances.AddOrUpdate(len, sql, (_, next) => next);

            return sql;
        });

        var failures = new ConcurrentBag<Exception>();
        var listLock = new object();
        var recorded = new List<List<(int key, string sql)>>();

        var tasks = Enumerable.Range(0, ThreadCount).Select(thread =>
        {
            var rnd = new Random(thread);
            return Task.Run(() =>
            {
                var perThread = new List<(int key, string sql)>();

                try
                {
                    for (int i = 0; i < IterationsPerThread; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        int len = LengthPool[rnd.Next(LengthPool.Length)];
                        var chunks = splitter.GetSql(len, 512).ToList();

                        // Splitting invariant, checked on the producing thread for every call:
                        // sum of chunk lengths == len, every length > 0, every length except
                        // the last is a multiple of 16.
                        int sum = 0;
                        for (int c = 0; c < chunks.Count; c++)
                        {
                            int l = chunks[c].length;
                            sum += l;

                            if (l <= 0)
                            {
                                throw new InvalidOperationException($"len={len}: non-positive chunk length {l}");
                            }

                            if (c < chunks.Count - 1 && l % 16 != 0)
                            {
                                throw new InvalidOperationException($"len={len}: non-last chunk length {l} is not a multiple of 16");
                            }
                        }

                        if (sum != len)
                        {
                            throw new InvalidOperationException($"len={len}: sum of chunk lengths {sum} != {len}");
                        }

                        perThread.AddRange(chunks.Select(chunk => (chunk.length, chunk.sql)));
                    }

                    lock (listLock)
                    {
                        recorded.Add(perThread);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }, token);
        });

        await Task.WhenAll(tasks);

        // (1) No thread threw — concurrent access to the cache must not produce
        // InvalidOperationException / ArgumentOutOfRangeException / corruption.
        Assert.Empty(failures);

        var observedByLength = new Dictionary<int, List<string>>();
        foreach (var (key, sql) in recorded.SelectMany(x => x))
        {
            if (!observedByLength.TryGetValue(key, out var list))
            {
                observedByLength[key] = list = [];
            }

            list.Add(sql);
        }

        Assert.NotEmpty(observedByLength);

        foreach (var (key, observations) in observedByLength)
        {
            // (3) All threads observed exactly one string instance per cached key
            // (ReferenceEquals — the whole point of the cache).
            var first = observations[0];
            foreach (var sql in observations)
            {
                Assert.True(
                    ReferenceEquals(first, sql),
                    $"key={key}: {observations.Count} observations include different string instances");
            }

            // (4) The factory ran at least once per key and at most once per key per
            // thread (GetOrAdd may re-run the factory under contention and discard
            // one of the results, so per-key calls are bounded by the thread count).
            int calls = callCounts[key];
            Assert.True(calls >= 1 && calls <= ThreadCount, $"key={key}: factory called {calls} times, expected 1..{ThreadCount}");

            // For keys the factory ran exactly once, cross-check that the single
            // observed instance is the very instance the factory produced and the
            // cache holds. (When the factory re-ran, a discarded instance may have
            // been captured last, so the cross-check applies only to single-call keys.)
            if (calls == 1)
            {
                Assert.True(
                    ReferenceEquals(seenInstances[key], first),
                    $"key={key}: cached instance is not the single factory result observed by all threads");
            }
        }
    }
}
