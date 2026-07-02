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
        // diff = 1100 - 1088 = 12
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(1100).ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(("sql-512", 512), result[0]);
        Assert.Equal(("sql-512", 512), result[1]);
        Assert.Equal(("sql-12", 12), result[2]);
    }


    [Fact]
    public void GetSql_DifferentLengths_NotCachedTogether()
    {
        var lengths = new List<int>();
        var splitter = new SqlCacheSplitter(len =>
        {
            lengths.Add(len);
            return $"sql-{len}";
        });

        _ = splitter.GetSql(10).ToList();  // 16
        _ = splitter.GetSql(20).ToList();  // 16 (cache hit)
        _ = splitter.GetSql(33).ToList();  // 32 + 1

        Assert.Contains(16, lengths);
        Assert.Contains(32, lengths);
        Assert.Contains(1, lengths);
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
        // diff = 528 - 528 = 0 → no remainder
        var splitter = new SqlCacheSplitter(len => $"sql-{len}");
        var result = splitter.GetSql(528).ToList();

        Assert.Single(result);
        Assert.Equal(("sql-512", 512), result[0]);
    }
}
