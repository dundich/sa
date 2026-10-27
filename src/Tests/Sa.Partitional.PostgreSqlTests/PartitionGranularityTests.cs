using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Cache;
using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSqlTests;

/// <summary>
/// Integration tests that run the same lifecycle — ensure, write, drop, archive — for every range
/// granularity, against three identically shaped tables that differ only in <see cref="PgPartBy"/>.
/// </summary>
/// <remarks>
/// <para>
/// The suite used to exercise <see cref="PgPartBy.Day"/> only, which is how a defect that made every
/// stored strategy resolve to <see cref="PgPartBy.Day"/> survived: a month-partitioned table was
/// checked against a one-day range, so its partitions looked missing on every write and the whole
/// partition DDL was re-run each time. Anything that depends on the granularity belongs in this matrix.
/// </para>
/// <para>
/// The fixture is shared by every case, so each test works under its own partition key: otherwise the
/// rows (and leaves) one case creates are visible to the next.
/// </para>
/// </remarks>
public class PartitionGranularityTests(PartitionGranularityTests.Fixture fixture)
    : IClassFixture<PartitionGranularityTests.Fixture>
{
    /// <summary>Midnight UTC of the current day — a deterministic instant inside every range.</summary>
    private static readonly DateTimeOffset Day = new(DateTime.UtcNow.Date, TimeSpan.Zero);

    /// <summary>
    /// A year and a day back: guaranteed to sit in a different day, month AND year, so dropping it
    /// exercises a different leaf for every granularity instead of the one being written to.
    /// </summary>
    private static readonly DateTimeOffset OlderDay = Day.AddYears(-1).AddDays(-1);

    private const string ArchiveSchema = "gran_archive";

    /// <summary>Partition key of one test: tenant 1 plus a part name unique to that test.</summary>
    private static StrOrNum[] Key(string part) => [1, part];

    public class Fixture : PgDataSourceFixture<IPartitionManager>
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    AddTable(schema, "gran_day", PgPartBy.Day);
                    AddTable(schema, "gran_month", PgPartBy.Month);
                    AddTable(schema, "gran_year", PgPartBy.Year);
                });
            })
            .AddDataSource(configure => configure.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)))
            ;
        }

        private static void AddTable(ISchemaBuilder schema, string name, PgPartBy partBy)
            => schema.AddTable(name,
                    "id INT NOT NULL",
                    "tenant_id INT NOT NULL",
                    "part TEXT NOT NULL",
                    "created_at BIGINT NOT NULL")
                .PartByList("tenant_id", "part")
                .TimestampAs("created_at")
                .PartByRange(partBy);
    }

    private IPartitionManager Parts => fixture.Sub;

    private IPartRepository Repository => fixture.ServiceProvider.GetRequiredService<IPartRepository>();

    private IPartCache Cache => fixture.ServiceProvider.GetRequiredService<IPartCache>();

    public static TheoryData<string, PartByRange> Granularities => new()
    {
        { "gran_day", PartByRange.Day },
        { "gran_month", PartByRange.Month },
        { "gran_year", PartByRange.Year },
    };

    #region helpers

    /// <summary>Rows of <paramref name="table"/> that belong to the given partition key.</summary>
    private Task<long> Rows(string table, string part, CancellationToken ct)
        => fixture.DataSource.ExecuteScalarTyped<long>(
            $"SELECT count(*) FROM \"public\".\"{table}\" WHERE tenant_id = 1 AND part = @part",
            [new NpgsqlParameter<string>("part", part)],
            ct);

    /// <summary>
    /// Names of every relation PostgreSQL created below <paramref name="table"/> — both the list levels
    /// (<c>table__{tenant}__{part}</c>) and the range leaves (<c>…__{dateSuffix}</c>).
    /// </summary>
    private async Task<string[]> Relations(string table, CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND starts_with(c.relname, @prefix)
            ORDER BY c.relname
            """;

        List<string> names = await fixture.DataSource.ExecuteReaderList(
            sql,
            static r => r.GetString(0),
            [new NpgsqlParameter<string>("prefix", $"{table}__")],
            ct);

        return [.. names];
    }

    /// <summary>
    /// The range leaves for <paramref name="value"/> at <paramref name="date"/>: every generated name
    /// ends with the granularity's date suffix, which is what separates a leaf from a list level.
    /// </summary>
    private static string[] RangeLeaves(
        string[] relations,
        PartByRange granularity,
        DateTimeOffset date,
        string part)
        => [.. relations.Where(n =>
            n.Contains($"__{part}__", StringComparison.Ordinal)
            && n.EndsWith(SuffixFor(granularity, date), StringComparison.Ordinal))];

    private Task InsertRow(string table, string part, int id, DateTimeOffset createdAt, CancellationToken ct)
        // Inserted into the ROOT: PostgreSQL routes the row to the matching leaf itself, so a missing
        // partition fails the statement instead of silently landing nowhere.
        => fixture.DataSource.ExecuteNonQuery(
            $"INSERT INTO \"public\".\"{table}\" (id, tenant_id, part, created_at) VALUES (@id, 1, @part, @at)",
            [
                new NpgsqlParameter<int>("id", id),
                new NpgsqlParameter<string>("part", part),
                new NpgsqlParameter<long>("at", createdAt.ToUnixTimeSeconds()),
            ],
            ct);

    private static string SuffixFor(PartByRange granularity, DateTimeOffset date) => granularity switch
    {
        PartByRange.Day => $"y{date:yyyy}m{date:MM}d{date:dd}",
        PartByRange.Month => $"y{date:yyyy}m{date:MM}",
        _ => $"y{date:yyyy}",
    };

    #endregion

    [Theory]
    [MemberData(nameof(Granularities))]
    public async Task EnsureParts_MakesThePartitionVisibleToTheCache(string table, PartByRange granularity)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        StrOrNum[] key = Key("cache");

        await Parts.EnsureParts(table, Day, key, ct);

        // The regression: before the fix the stored strategy of a month/year table resolved to Day, so
        // `InCache` only matched on the first day of the period and every write re-ran the DDL.
        Assert.True(await Cache.InCache(table, Day, key, ct), $"{table} ({granularity}) is not in the cache");

        // A different key is a different partition, so it must still be a miss.
        Assert.False(await Cache.InCache(table, Day, Key("other"), ct));
    }

    [Theory]
    [MemberData(nameof(Granularities))]
    public async Task EnsureParts_IsIdempotent(string table, PartByRange granularity)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        StrOrNum[] key = Key("idem");

        await Parts.EnsureParts(table, Day, key, ct);
        int afterFirst = (await Relations(table, ct)).Length;

        await Parts.EnsureParts(table, Day, key, ct);
        int afterSecond = (await Relations(table, ct)).Length;

        Assert.Equal(afterFirst, afterSecond);
    }

    [Theory]
    [MemberData(nameof(Granularities))]
    public async Task Migrate_NamesTheLeafAfterTheGranularity(string table, PartByRange granularity)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Repository.Migrate([Day], _ => Task.FromResult<StrOrNum[][]>([Key("mig")]), ct);
        // A second pass must be a no-op, not a duplicate.
        await Repository.Migrate([Day], _ => Task.FromResult<StrOrNum[][]>([Key("mig")]), ct);

        string leaf = Assert.Single(RangeLeaves(await Relations(table, ct), granularity, Day, "mig"));
        Assert.Contains("__mig__", leaf, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Granularities))]
    public async Task WrittenRow_LandsInTheEnsuredPartition(string table, PartByRange granularity)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Parts.EnsureParts(table, Day, Key("write"), ct);
        await InsertRow(table, "write", id: 1, Day.AddHours(12), ct);

        Assert.Equal(1L, await Rows(table, "write", ct));
    }

    [Theory]
    [MemberData(nameof(Granularities))]
    public async Task DropPartsToDate_RemovesTheOldLeafAndKeepsTheRecentOne(string table, PartByRange granularity)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Parts.EnsureParts(table, OlderDay, Key("drop"), ct);
        await Parts.EnsureParts(table, Day, Key("drop"), ct);
        await InsertRow(table, "drop", id: 1, OlderDay.AddHours(12), ct);
        await InsertRow(table, "drop", id: 2, Day.AddHours(12), ct);

        await Repository.DropPartsToDate(table, Day, ct);

        // The older leaf is gone with its row, the recent one survives.
        Assert.Equal(1L, await Rows(table, "drop", ct));
        Assert.Empty(RangeLeaves(await Relations(table, ct), granularity, OlderDay, "drop"));
        Assert.Single(RangeLeaves(await Relations(table, ct), granularity, Day, "drop"));
    }

    [Theory]
    [MemberData(nameof(Granularities))]
    public async Task ArchivePartsToDate_MovesTheOldLeafAndIsRepeatable(string table, PartByRange granularity)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Parts.EnsureParts(table, OlderDay, Key("arch"), ct);
        await InsertRow(table, "arch", id: 1, OlderDay.AddHours(12), ct);

        int archived = await Repository.ArchivePartsToDate(table, Day, ArchiveSchema, ct);
        Assert.Equal(1, archived);

        // A second pass has nothing left to do: the metadata row is archived and the query filters it.
        int again = await Repository.ArchivePartsToDate(table, Day, ArchiveSchema, ct);
        Assert.Equal(0, again);

        // The leaf kept its name, moved to the archive schema, and the row is still readable there.
        string archivedLeaf = $"{ArchiveSchema}.\"{table}__1__arch__{SuffixFor(granularity, OlderDay)}\"";

        long survived = await fixture.DataSource.ExecuteScalarTyped<long>(
            $"SELECT count(*) FROM {archivedLeaf}", cancellationToken: ct);

        Assert.Equal(1L, survived);
    }
}
