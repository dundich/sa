using Npgsql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests;

/// <summary>
/// Integration companion to the string-level escaping tests: the SQL generated for hostile names is
/// actually <b>executed</b> by PostgreSQL and the resulting partition is attached to its parent.
/// </summary>
/// <remarks>
/// The escaping tests assert on the generated text, which pins <i>how</i> a name is quoted; they cannot
/// catch a statement that is quoted correctly but still invalid as a whole, nor a name that only breaks
/// on the way back (cache row, <c>regclass</c> lookup, <c>DROP</c>). Reading the identifier back from
/// <c>pg_inherits</c> is what proves the round trip.
/// </remarks>
public class SqlSmokeTests(SqlSmokeTests.Fixture fixture) : IClassFixture<SqlSmokeTests.Fixture>
{
    private static readonly DateTimeOffset Day = new(DateTime.UtcNow.Date, TimeSpan.Zero);

    public class Fixture : PgDataSourceFixture<IPartRepository>
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("smoke",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL")
                    .PartByList("part")
                    .TimestampAs("date");

                    // A quote in the table name: the identifier has to be escaped on every path that
                    // reads the name back (create, cache row, drop).
                    schema.AddTable("sm\"oke",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL")
                    .PartByList("part")
                    .TimestampAs("date");
                });

                // A quote in the schema name as well.
                builder.AddSchema("sc\"h", schema =>
                {
                    schema.AddTable("t",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL")
                    .PartByList("part")
                    .TimestampAs("date");
                });
            })
            .AddDataSource(configure => configure.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)))
            ;
        }
    }

    private IPartRepository Parts => fixture.Sub;

    /// <summary>Number of partitions attached to the given root (quoted, schema-qualified).</summary>
    private Task<long> Children(string quotedRoot, CancellationToken ct)
        => fixture.DataSource.ExecuteScalarTyped<long>(
            "SELECT count(*) FROM pg_inherits WHERE inhparent = to_regclass(@root)",
            [new NpgsqlParameter<string>("root", quotedRoot)],
            ct);

    /// <summary>Whether a (quoted, schema-qualified) relation exists.</summary>
    private Task<bool> Exists(string quotedName, CancellationToken ct)
        => fixture.DataSource.ExecuteScalarTyped<bool>(
            "SELECT to_regclass(@name) IS NOT NULL",
            [new NpgsqlParameter<string>("name", quotedName)],
            ct);

    private static string DaySuffix(DateTimeOffset date) => $"y{date:yyyy}m{date:MM}d{date:dd}";

    /// <summary>
    /// Values that are legal inside a quoted identifier but hostile to a naive one: each one is either
    /// rejected by the server or silently changes the statement if the quoting is wrong.
    /// </summary>
    public static TheoryData<string> HostileValues => new()
    {
        "plain",
        "a\"b",
        "a'b",
        @"a\b",
        "a b",
        "кириллица",
        "x\"; DROP TABLE users; --",
        "a,b",
        "a--b",
        "$dollar$",
        "%",
    };

    [Theory]
    [MemberData(nameof(HostileValues))]
    public async Task CreatePart_ExecutesAndAttachesExactlyOnePartition(string value)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string root = "\"public\".\"smoke\"";

        long before = await Children(root, ct);

        await Parts.CreatePart("smoke", Day, [value], ct);
        // Idempotent: the DDL is CREATE TABLE IF NOT EXISTS, so a second call must not add a sibling.
        await Parts.CreatePart("smoke", Day, [value], ct);

        Assert.Equal(before + 1, await Children(root, ct));
    }

    [Fact]
    public async Task CreatePart_ExecutesForATableNameWithAQuote()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string root = "\"public\".\"sm\"\"oke\"";

        long before = await Children(root, ct);

        await Parts.CreatePart("sm\"oke", Day, ["v"], ct);
        await Parts.CreatePart("sm\"oke", Day, ["v"], ct);

        Assert.Equal(before + 1, await Children(root, ct));

        // The cache row was written under the same identifier the DDL created, so the part list is
        // readable and the DROP below can find it again.
        List<PartByRangeInfo> cached = await Parts.GetPartsFromDate("sm\"oke", Day.AddDays(-1), ct);
        Assert.Contains(cached, p => p.Id.Contains("sm\"\"oke__v__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DropPart_ExecutesForATableNameWithAQuote()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A month back, so this leaf is the only one the drop can match: the other tests in the class
        // share the fixture and create their leaves for today.
        DateTimeOffset oldDay = Day.AddDays(-30);
        string quotedLeaf = $"\"public\".\"sm\"\"oke__drop-me__{DaySuffix(oldDay)}\"";

        await Parts.CreatePart("sm\"oke", oldDay, ["drop-me"], ct);
        Assert.True(await Exists(quotedLeaf, ct), "the partition under test was not created");

        // Drops the leaf (its name is read back from the cache table) and its cache row.
        int dropped = await Parts.DropPartsToDate("sm\"oke", oldDay.AddDays(1), ct);

        Assert.Equal(1, dropped);
        Assert.False(await Exists(quotedLeaf, ct), "the partition survived its DROP");

        // The cache row went with it, so a later pass cannot try to drop the table again.
        List<PartByRangeInfo> cached = await Parts.GetPartsFromDate("sm\"oke", oldDay.AddDays(-1), ct);
        Assert.DoesNotContain(cached, p => p.Id.Contains("drop-me", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatePart_ExecutesForASchemaNameWithAQuote()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string root = "\"sc\"\"h\".\"t\"";

        await Parts.CreatePart("sc\"h.t", Day, ["v"], ct);

        Assert.Equal(1L, await Children(root, ct));
    }
}
