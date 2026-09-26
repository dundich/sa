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
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(1100).ToList();

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
        // 512 → multipleOf16 = 512, which equals maxLen → single chunk
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(512).ToList();

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
        var result = splitter.GetSql(528).ToList();

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
        var result = splitter.GetSql(1000).ToList();

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
        var result = splitter.GetSql(513).ToList();

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
        var result = splitter.GetSql(2048).ToList();

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
            527, 528, 543, 544, 600, 1000, 1024, 1039, 1100,
            1536, 1551, 2048, 4096
        };
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");

        foreach (var len in lens)
        {
            var result = splitter.GetSql(len).ToList();

            // sum of all yielded lengths == len
            var sum = result.Sum(r => r.length);
            Assert.True(sum == len, $"len={len}: sum of lengths {sum} != {len}");

            // every length is in (0, maxLen]
            foreach (var (sql, l) in result)
            {
                Assert.True(l > 0 && l <= 512, $"len={len}: length {l} not in (0, 512]");
            }

            // all lengths except the last are multiples of 16
            for (int i = 0; i < result.Count - 1; i++)
            {
                Assert.True(result[i].length % 16 == 0, $"len={len}: non-last length {result[i].length} not multiple of 16");
            }
        }
    }
}
