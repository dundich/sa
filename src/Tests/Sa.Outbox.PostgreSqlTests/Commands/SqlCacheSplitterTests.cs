using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSqlTests.Commands;

public class SqlCacheSplitterTests
{
    [Fact]
    public void GetSql_EmptyLength_YieldsNothing()
    {
        var splitter = new SqlCacheSplitter(_ => "mock");
        var result = splitter.GetSql(0).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void GetSql_NegativeLength_YieldsNothing()
    {
        var splitter = new SqlCacheSplitter(_ => "mock");
        var result = splitter.GetSql(-5).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void GetSql_SingleChunk_ReturnsOneItem()
    {
        int capturedLen = 0;
        var splitter = new SqlCacheSplitter(len =>
        {
            capturedLen = len;
            return $"sql-{len}";
        });

        var result = splitter.GetSql(10).ToList();

        Assert.Single(result);
        Assert.Equal(("sql-10", 10), result[0]);
        Assert.Equal(10, capturedLen);
    }

    [Fact]
    public void GetSql_ExactlyMultipleOf16_ReturnsSingleChunk()
    {
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(48).ToList();

        Assert.Single(result);
        Assert.Equal(("sql-48", 48), result[0]);
    }

    [Fact]
    public void GetSql_RemainingAfterMultiple16_AddsDiff()
    {
        // 25 → multipleOf16 = 16, diff = 9 → two chunks: 16 + 9
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(25).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(("sql-16", 16), result[0]);
        Assert.Equal(("sql-9", 9), result[1]);
    }


    [Fact]
    public void GetSql_VeryLarge_SplitsByMaxLen()
    {
        // 1100 → multipleOf16 = 1088, maxLen = 512 → 1088/512 = 2 → 512 + 512
        // rest = 1088 - 2*512 = 64
        // diff = 1100 - 1088 = 12
        // Explicit maxLen: this is an algorithm test, not a pin of the production value
        // (that is DefaultMaxLen, covered by DefaultMaxLen_Is1024... below).
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(1100, maxLen: 512).ToList();

        Assert.Equal(4, result.Count);
        Assert.Equal(("sql-512", 512), result[0]);
        Assert.Equal(("sql-512", 512), result[1]);
        Assert.Equal(("sql-64", 64), result[2]);
        Assert.Equal(("sql-12", 12), result[3]);
    }


    [Fact]
    public void GetSql_CacheReuse_DoesNotRegenerateSameLength()
    {
        var callCount = 0;
        var splitter = new SqlCacheSplitter(_ => { ++callCount; return ""; });

        _ = splitter.GetSql(48).ToList();  // generates sql-48
        _ = splitter.GetSql(48).ToList();  // cache hit → no regeneration
        _ = splitter.GetSql(32).ToList();  // different length → regenerates

        Assert.Equal(2, callCount);
    }

    [Fact]
    public void GetSql_LenEqualsMaxLen_BoundaryBehavior()
    {
        // 512 → multipleOf16 = 512, which equals maxLen (explicit, see the note in
        // GetSql_VeryLarge_SplitsByMaxLen) → single chunk
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(512, maxLen: 512).ToList();

        Assert.Single(result);
        Assert.Equal(("sql-512", 512), result[0]);
    }

    [Fact]
    public void GetSql_JustAboveMaxLen_TriggersMultiSplit()
    {
        // 528 → multipleOf16 = 528, > 512 → 528/512 = 1 → one 512
        // rest = 528 - 1*512 = 16
        // diff = 528 - 528 = 0 → no remainder
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(528, maxLen: 512).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(("sql-512", 512), result[0]);
        Assert.Equal(("sql-16", 16), result[1]);
    }

    [Fact]
    public void GetSql_MidRange_KeepsAllBytes()
    {
        // 1000 → multipleOf16 = 992, maxLen = 512 → 992/512 = 1 → one 512
        // rest = 992 - 512 = 480
        // diff = 1000 - 992 = 8
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(1000, maxLen: 512).ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(("sql-512", 512), result[0]);
        Assert.Equal(("sql-480", 480), result[1]);
        Assert.Equal(("sql-8", 8), result[2]);
    }

    [Fact]
    public void GetSql_JustBelowMaxLen_PlusOne_UseElseBranch()
    {
        // 513 → multipleOf16 = 512, which is NOT > 512 → else branch → 512
        // diff = 513 - 512 = 1
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(513, maxLen: 512).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(("sql-512", 512), result[0]);
        Assert.Equal(("sql-1", 1), result[1]);
    }

    [Fact]
    public void GetSql_ExactMultipleOfMaxLen_NoRest()
    {
        // 2048 → multipleOf16 = 2048, maxLen = 512 → 2048/512 = 4 → 4 × 512
        // rest = 2048 - 4*512 = 0, diff = 0
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(2048, maxLen: 512).ToList();

        Assert.Equal(4, result.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(("sql-512", 512), result[i]);
        }
    }

    [Fact]
    public void GetSql_Invariant_SumEqualsLen_EachAtMostMax_NonLastMultipleOf16()
    {
        var lens = new[]
        {
            1, 15, 16, 17, 25, 48, 255, 256, 511, 512, 513,
            527, 528, 543, 544, 600, 1000, 1023, 1024, 1025, 1039, 1100,
            1536, 1551, 2048, 2049, 4096
        };
        int maxLen = SqlCacheSplitter.DefaultMaxLen;
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");

        foreach (var len in lens)
        {
            var result = splitter.GetSql(len, maxLen).ToList();

            // sum of all yielded lengths == len
            var sum = result.Sum(r => r.length);
            Assert.True(sum == len, $"len={len}: sum of lengths {sum} != {len}");

            // every length is in (0, maxLen]
            foreach (var (sql, l) in result)
            {
                Assert.True(l > 0 && l <= maxLen, $"len={len}: length {l} not in (0, {maxLen}]");
            }

            // all lengths except the last are multiples of 16
            for (int i = 0; i < result.Count - 1; i++)
            {
                Assert.True(result[i].length % 16 == 0, $"len={len}: non-last length {result[i].length} not multiple of 16");
            }
        }
    }

    [Fact]
    public void DefaultMaxLen_Is1024_AndDrivesTheParameterNameCache()
    {
        // M8/Q4, decided 2026-09-29: the chunk ceiling was raised from 512 to 1024 after
        // measuring the real per-row parameter ratio. Verified numbers (Pinned in
        // SqlOutboxBuilderTests.BulkStatements_ParameterPerRowRatio_Is8And4_Not16):
        // a 512-row finish chunk binds 8×512+4 = 4100 parameters (6.3% of 65 535),
        // leaving ~16× unused headroom while MaxBatchSize=1024 consumers paid 2 round
        // trips per finish. At 1024 the bind is 8×1024+4 = 8196 (12.5%) and the shipped
        // 1024 preset completes in one round trip. The PostgreSQL ceiling for a single
        // statement is ~8190 rows, so 1024 keeps an 8× margin.
        Assert.Equal(1024, SqlCacheSplitter.DefaultMaxLen);

        // The parameter-name cache pre-allocates names only up to MaxIndex; a chunk at or
        // above it silently falls back to string interpolation. Sharing one constant makes
        // divergence impossible — this asserts the coupling holds.
        Assert.Equal(SqlCacheSplitter.DefaultMaxLen, NpgsqlCommandExtension.BatchParams.MaxIndex);
    }

    [Fact]
    public void GetSql_DefaultMaxLen_1024BatchIsOneChunk_1025Splits()
    {
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");

        // The 1024 preset: one round trip instead of the pre-raise two chunks of 512.
        var at = splitter.GetSql(1024).ToList();
        Assert.Single(at);
        Assert.Equal(("sql-1024", 1024), at[0]);

        // Just above the default ceiling: 1024 + the 1-row remainder.
        var above = splitter.GetSql(1025).ToList();
        Assert.Equal(2, above.Count);
        Assert.Equal(("sql-1024", 1024), above[0]);
        Assert.Equal(("sql-1", 1), above[1]);
    }
}
