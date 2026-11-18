using Microsoft.Extensions.Logging.Abstractions;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Cleaning;

namespace Sa.Partitional.PostgreSqlTests.Cleaning;

/// <summary>
/// Cleanup isolates a failure per table (same rule as the migration): one broken table is logged and
/// the rest of the module is still cleaned, and only then does the run report the first failure.
/// </summary>
public class PartCleanupIsolationTests
{
    [Fact]
    public async Task OneFailingTable_DoesNotStarveTheOthers_ButTheRunStillFails()
    {
        FakeRepository repository = new(failing: "public.bad");

        PartCleanupService service = new(
            repository,
            new PartCleanupScheduleSettings(),
            new FakeSqlBuilder("public.bad", "public.good"),
            NullLogger<PartCleanupService>.Instance);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CleanAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        Assert.Equal("public.bad failed", ex.Message);
        Assert.Equal(["public.good"], repository.Dropped);
    }

    [Fact]
    public async Task AllTablesHealthy_ReturnsTheSummedCount()
    {
        FakeRepository repository = new(failing: null);

        PartCleanupService service = new(
            repository,
            new PartCleanupScheduleSettings(),
            new FakeSqlBuilder("public.a", "public.b"),
            NullLogger<PartCleanupService>.Instance);

        int removed = await service.CleanAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(2, removed);
        Assert.Equal(["public.a", "public.b"], repository.Dropped);
    }

    private sealed class FakeRepository(string? failing) : IPartRepository
    {
        public List<string> Dropped { get; } = [];

        public Task<int> DropPartsToDateAsync(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
        {
            if (tableName == failing) throw new InvalidOperationException($"{tableName} failed");

            Dropped.Add(tableName);
            return Task.FromResult(1);
        }

        public Task<int> CreatePartAsync(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> MigrateAsync(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> MigrateAsync(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<PartByRangeInfo>> GetPartsFromDate(string tableName, DateTimeOffset fromDate, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<PartByRangeInfo>> GetPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ArchivePartsToDateAsync(string tableName, DateTimeOffset toDate, string archiveSchemaName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> ArchivePartsToDateAsync(string tableName, DateTimeOffset toDate, string archiveSchemaName, ArchiveCollisionMode collisionMode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> PruneEmptyListPartsAsync(string tableName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeSqlBuilder(params string[] tables) : ISqlBuilder
    {
        public IReadOnlyCollection<ISqlTableBuilder> Tables { get; } = [.. tables.Select(name => new FakeTable(name))];

        public ISqlTableBuilder? this[string tableName] => null;

        public IAsyncEnumerable<MigrateBatch> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve)
            => throw new NotSupportedException();

        public string SelectPartsFromDateSql(string tableName) => throw new NotSupportedException();

        public string SelectPartsToDateSql(string tableName) => throw new NotSupportedException();
    }

    private sealed class FakeTable(string fullName) : ISqlTableBuilder
    {
        public string FullName => fullName;

        public ITableSettings Settings => throw new NotSupportedException();

        public PgPartBy PartBy => PgPartBy.Day;

        public string SelectPartsFromDate => throw new NotSupportedException();

        public string SelectPartsToDate => throw new NotSupportedException();

        public string CreateSql(DateTimeOffset date, params StrOrNum[] partValues) => throw new NotSupportedException();

        public string CreatePartSql(DateTimeOffset date, StrOrNum[] partValues) => throw new NotSupportedException();

        public string CreateRootSql() => throw new NotSupportedException();

        public string CreateCacheSql() => throw new NotSupportedException();
    }
}
