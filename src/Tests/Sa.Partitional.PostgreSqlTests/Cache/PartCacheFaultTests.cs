using Npgsql;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Cache;
using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSqlTests.Cache;

/// <summary>
/// Unit tests (no database) for <see cref="PartCache"/> faulted-task recovery:
/// a faulted (or cancelled) cached task must be evicted so the next call
/// re-queries the database instead of rethrowing the stale exception forever.
/// </summary>
public class PartCacheFaultTests
{
    private const string Table = "test";

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static readonly StrOrNum[] PartValues = [1, "some"];

    private static readonly PartCacheSettings Settings = new();

    // Cached window start: (FixedNow - CachedFromDate).StartOfDay()
    private static readonly DateTimeOffset ExpectedFrom = (FixedNow - Settings.CachedFromDate).StartOfDay();

    private static PostgresException PgError(string sqlState) => new("simulated", "ERROR", "ERROR", sqlState);

    [Fact]
    public async Task InCache_TransientError_RecoversOnNextCall()
    {
        FakePartRepository repository = new();
        repository.EnqueueGetParts(_ => throw PgError(PostgresErrorCodes.QueryCanceled));
        repository.EnqueueGetParts(_ => Task.FromResult(new List<PartByRangeInfo> { MatchingPart(repository) }));

        PartCache cache = CreateCache(repository);

        // 1st call: the only cached task faults — the caller gets the original exception.
        await Assert.ThrowsAsync<PostgresException>(() => cache.InCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken));

        // 2nd call (same table/date/partValues): the faulted entry is evicted and
        // re-queried — before the fix this rethrew the stale 57014 forever.
        bool actual = await cache.InCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken);

        Assert.True(actual);
        Assert.Equal(2, repository.GetPartsFromDateCalls);
    }

    [Fact]
    public async Task EnsureCache_TransientError_RecoversAndCreatesPart()
    {
        FakePartRepository repository = new();
        repository.EnqueueGetParts(_ => throw PgError(PostgresErrorCodes.QueryCanceled));
        repository.EnqueueGetParts(_ => Task.FromResult(new List<PartByRangeInfo>()));                          // part not visible yet
        repository.EnqueueGetParts(_ => Task.FromResult(new List<PartByRangeInfo> { MatchingPart(repository) })); // part visible after creation

        PartCache cache = CreateCache(repository);

        // 1st EnsureCache: InCache faults and the exception propagates —
        // CreatePart must not be attempted on the broken path.
        await Assert.ThrowsAsync<PostgresException>(() => cache.EnsureCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken));
        Assert.Equal(0, repository.CreatePartCalls);

        // 2nd EnsureCache: cache recovers, partition is created and the final InCache confirms it.
        bool actual = await cache.EnsureCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken);

        Assert.True(actual);
        Assert.Equal(1, repository.CreatePartCalls);
    }

    [Fact]
    public async Task InCache_UndefinedTable_ReturnsFalse_AndStaysCached()
    {
        FakePartRepository repository = new();
        repository.EnqueueGetParts(_ => throw PgError(PostgresErrorCodes.UndefinedTable));

        PartCache cache = CreateCache(repository);

        // 42P01 is swallowed: an empty (successful) result is cached, not the failure.
        bool actual = await cache.InCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken);

        Assert.False(actual);

        // The successful empty list must remain cached: no re-query on the 2nd call (regression check).
        actual = await cache.InCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken);

        Assert.False(actual);
        Assert.Equal(1, repository.GetPartsFromDateCalls);
    }

    [Fact]
    public async Task InCache_InvalidSchemaName_ReturnsFalse()
    {
        FakePartRepository repository = new();
        repository.EnqueueGetParts(_ => throw PgError(PostgresErrorCodes.InvalidSchemaName));

        PartCache cache = CreateCache(repository);

        // 3F000 is swallowed as well: returns false without throwing.
        bool actual = await cache.InCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken);

        Assert.False(actual);
        Assert.Equal(1, repository.GetPartsFromDateCalls);
    }

    [Fact]
    public async Task InCache_ParallelCallers_AllRecover()
    {
        FakePartRepository repository = new();

        // Phase 1 (single caller): transient error faults the only cached task —
        // the triggering caller receives the original exception.
        repository.EnqueueGetParts(_ => throw PgError(PostgresErrorCodes.QueryCanceled));

        PartCache cache = CreateCache(repository);

        await Assert.ThrowsAsync<PostgresException>(() => cache.InCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken));
        Assert.Equal(1, repository.GetPartsFromDateCalls);

        // Phase 2 (8 parallel callers): every caller sees the faulted entry and must
        // recover through TryRemove + GetOrAdd — none of them may observe the stale 57014.
        repository.EnqueueGetParts(_ => Task.FromResult(new List<PartByRangeInfo> { MatchingPart(repository) }));

        Task<bool>[] tasks =
        [
            ..Enumerable.Range(0, 8)
                .Select(_ => cache.InCache(Table, ExpectedFrom, PartValues, TestContext.Current.CancellationToken))
        ];

        bool[] results = await Task.WhenAll(tasks);

        Assert.All(results, actual => Assert.True(actual));
        Assert.Equal(2, repository.GetPartsFromDateCalls);
    }

    private static PartCache CreateCache(FakePartRepository repository)
        => new(repository, new FakeSqlBuilder(), Settings, new FixedTimeProvider(FixedNow));

    private static PartByRangeInfo MatchingPart(FakePartRepository repository)
        => new(
            Id: $"{Table}_y2026m09d24",
            RootTableName: Table,
            PartValues: PartValues,
            PartBy: PgPartBy.Day,
            FromDate: repository.LastFrom ?? ExpectedFrom);

    /// <summary>
    /// Fixed clock so the cache window start (<c>(now - CachedFromDate).StartOfDay()</c>)
    /// is deterministic across all tests.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset fixedUtcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixedUtcNow;
    }
}

internal sealed class FakePartRepository : IPartRepository
{
    private readonly Queue<Func<CancellationToken, Task<List<PartByRangeInfo>>>> _getPartsFromDateBehaviors = new();

    public int GetPartsFromDateCalls { get; private set; }

    public int CreatePartCalls { get; private set; }

    /// <summary>The <c>fromDate</c> captured from the last <see cref="GetPartsFromDate"/> call.</summary>
    public DateTimeOffset? LastFrom { get; private set; }

    public void EnqueueGetParts(Func<CancellationToken, Task<List<PartByRangeInfo>>> behavior) => _getPartsFromDateBehaviors.Enqueue(behavior);

    public async Task<List<PartByRangeInfo>> GetPartsFromDate(string tableName, DateTimeOffset fromDate, CancellationToken cancellationToken = default)
    {
        GetPartsFromDateCalls++;
        LastFrom = fromDate;
        return await _getPartsFromDateBehaviors.Dequeue()(cancellationToken).ConfigureAwait(false);
    }

    public Task<int> CreatePart(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
    {
        CreatePartCalls++;
        return Task.FromResult(1);
    }

    public Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<int> Migrate(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<List<PartByRangeInfo>> GetPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<PartByRangeInfo>());

    public Task<int> DropPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}

internal sealed class FakeSqlBuilder : ISqlBuilder
{
    private readonly FakeSqlTableBuilder _testTable = new();

    public ISqlTableBuilder? this[string tableName] => tableName == "test" ? _testTable : null;

    public IReadOnlyCollection<ISqlTableBuilder> Tables { get; } = Array.Empty<ISqlTableBuilder>();

    public IAsyncEnumerable<string> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve)
        => throw new NotImplementedException();

    public string CreatePartSql(string tableName, DateTimeOffset date, StrOrNum[] partValues)
        => string.Empty;

    public string SelectPartsQualifiedTablesSql(string tableName, StrOrNum[] partValues)
        => string.Empty;

    public string SelectPartsQualifiedTablesSql(string qualifiedTablesSql)
        => string.Empty;

    public string SelectPartsFromDateSql(string tableName)
        => string.Empty;

    public string SelectPartsToDateSql(string tableName)
        => string.Empty;
}

internal sealed class FakeSqlTableBuilder : ISqlTableBuilder
{
    public string FullName => "public.test";

    public ITableSettings Settings => throw new NotImplementedException();

    public string SelectPartsFromDate => string.Empty;

    public string SelectPartsToDate => string.Empty;

    public string GetPartsSql(StrOrNum[] partValues) => string.Empty;

    public string CreateSql(DateTimeOffset date, params StrOrNum[] partValues) => string.Empty;
}
