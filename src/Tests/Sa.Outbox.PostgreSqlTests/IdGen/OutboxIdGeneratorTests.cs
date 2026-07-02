using Sa.Outbox.PostgreSql.IdGen;

namespace Sa.Outbox.PostgreSqlTests.IdGen;

public class OutboxIdGeneratorTests
{
    private readonly IOutboxIdGenerator _sut = new OutboxIdGenerator();

    [Fact]
    public void GenId_ReturnsValidGuid()
    {
        var id = _sut.GenId(DateTimeOffset.UtcNow);
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public void GenId_TimestampOrdering_PreservesChronologicalOrder()
    {
        var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id1 = _sut.GenId(baseTime);
        var id2 = _sut.GenId(baseTime.AddMilliseconds(1));
        var id3 = _sut.GenId(baseTime.AddSeconds(1));

        Assert.True(id1 < id2, "Earlier timestamp should produce smaller UUID v7");
        Assert.True(id2 < id3, "Later timestamp should produce larger UUID v7");
    }

    [Fact]
    public void GenId_SameTimestamp_ProducesDifferentGuids()
    {
        var sameTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var id1 = _sut.GenId(sameTime);
        var id2 = _sut.GenId(sameTime);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GenId_WithPastTimestamp_ValidUuid()
    {
        var past = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = _sut.GenId(past);
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public void GenId_WithFutureTimestamp_ValidUuid()
    {
        var future = new DateTimeOffset(2100, 12, 31, 23, 59, 59, TimeSpan.FromHours(3));
        var id = _sut.GenId(future);
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public void GenId_ConsecutiveCalls_IncreasingOrder()
    {
        var ids = new List<Guid>();
        var startTime = DateTimeOffset.UtcNow;

        for (int i = 0; i < 100; i++)
        {
            ids.Add(_sut.GenId(startTime.AddMilliseconds(i)));
        }

        Guid[] sorted = [.. ids];
        Array.Sort(sorted);

        Assert.Equal(ids, sorted);
    }
}
