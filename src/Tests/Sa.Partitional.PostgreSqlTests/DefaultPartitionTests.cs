using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests;

/// <summary>
/// Integration tests for the optional <c>DEFAULT</c> partition, created at every partitioning level.
/// They cover both routing of unmatched rows into the catch-all and the harder case where the
/// catch-all already holds rows that a later-created partition must take over (PostgreSQL refuses to
/// create such a partition while the default still carries conflicting rows).
/// </summary>
public class DefaultPartitionTests(DefaultPartitionTests.Fixture fixture)
    : IClassFixture<DefaultPartitionTests.Fixture>
{
    public class Fixture : PgDataSourceFixture<IPartitionManager>
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    // Two list levels + a range leaf, with a DEFAULT at each of the three parents.
                    schema.AddTable("def_nested",
                        "id INT NOT NULL",
                        "country TEXT NOT NULL",
                        "city TEXT NOT NULL"
                    )
                    .PartByList("country", "city")
                    .PartByRange(PgPartBy.Day)
                    .AddMigration("RU", "Moscow")
                    .WithDefaultPartition();

                    // No list columns: the root is range-partitioned, so the default catches dates
                    // outside every created range.
                    schema.AddTable("def_range",
                        "id INT NOT NULL"
                    )
                    .PartByRange(PgPartBy.Day)
                    .WithDefaultPartition();

                    // An ALWAYS identity makes the reconciliation's move-back insert explicit ids;
                    // only `OVERRIDING SYSTEM VALUE` keeps that legal (error 22014 without it).
                    schema.AddTable("def_always",
                        "id INT GENERATED ALWAYS AS IDENTITY"
                    )
                    .PartByRange(PgPartBy.Day)
                    .WithDefaultPartition();
                });
            })
            .AddDataSource(configure => configure.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)))
            ;
        }
    }

    private IPartitionManager Sub => fixture.Sub;

    /// <summary>Midnight UTC of the current day — a deterministic instant inside the migrated range.</summary>
    private static readonly DateTimeOffset Day = new(DateTime.UtcNow.Date, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDay = Day.AddDays(1);

    private async Task<long> Count(string table, CancellationToken ct)
        => await fixture.DataSource.ExecuteScalarTyped<long>($"SELECT count(*) FROM {table};", cancellationToken: ct);


    [Fact]
    public async Task UnlistedListValue_LandsInTheRootDefault()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Sub.Migrate([Day], ct);

        // country = US has no list partition; it belongs to the root catch-all.
        await fixture.DataSource.ExecuteNonQuery(
            $"INSERT INTO public.def_nested (id, country, city, created_at) VALUES (1, 'US', 'NY', {Day.ToUnixTimeSeconds()});", ct);

        Assert.Equal(1L, await Count("\"public\".\"def_nested__def$\"", ct));
    }

    [Fact]
    public async Task RangeOnlyTable_OutOfRangeDate_LandsInTheDefault()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Sub.Migrate([Day], ct);

        // Only the current day was migrated; 2000 is outside every range.
        long year2000 = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        await fixture.DataSource.ExecuteNonQuery(
            $"INSERT INTO public.def_range (id, created_at) VALUES (1, {year2000});", ct);

        Assert.Equal(1L, await Count("\"public\".\"def_range__def$\"", ct));
    }

    [Fact]
    public async Task PartitionCreatedLater_TakesOverRowsAlreadySittingInTheDefault()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Day is migrated, NextDay is not: the row lands in the range-level default of RU/Moscow.
        await Sub.Migrate([Day], ct);
        await fixture.DataSource.ExecuteNonQuery(
            $"INSERT INTO public.def_nested (id, country, city, created_at) VALUES (1, 'RU', 'Moscow', {NextDay.ToUnixTimeSeconds()});", ct);

        Assert.Equal(1L, await Count("\"public\".\"def_nested__RU__Moscow__def$\"", ct));

        // Creating the NextDay leaf would be refused while the default still holds the row; the
        // migration pulls it out, creates the partition, and puts it back.
        await Sub.Migrate([NextDay], ct);

        Assert.Equal(0L, await Count("\"public\".\"def_nested__RU__Moscow__def$\"", ct));
        Assert.Equal(1L, await Count($"\"public\".\"def_nested__RU__Moscow__{PgPartBy.Day.Fmt(NextDay)}\"", ct));
    }

    [Fact]
    public async Task PartitionCreatedLater_TakesOverRowsWithAnAlwaysIdentityId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Day is migrated, WeekLater is not: the row lands in the root default (outside the range).
        // A distinct date keeps the migration-driven tests independent: Migrate() migrates every
        // table in the shared fixture, so migrating NextDay here would pre-create the leaf the
        // sibling test relies on staying absent.
        await Sub.Migrate([Day], ct);
        DateTimeOffset weekLater = Day.AddDays(7);
        await fixture.DataSource.ExecuteNonQuery(
            $"INSERT INTO public.def_always (created_at) VALUES ({weekLater.ToUnixTimeSeconds()});", ct);

        Assert.Equal(1L, await Count("\"public\".\"def_always__def$\"", ct));

        // The reconciliation puts the staged id back into a GENERATED ALWAYS AS IDENTITY column;
        // without the OVERRIDING clause PostgreSQL would refuse it (22014) and fail the migration.
        await Sub.Migrate([weekLater], ct);

        Assert.Equal(0L, await Count("\"public\".\"def_always__def$\"", ct));

        long leafId = await fixture.DataSource.ExecuteScalarTyped<long>(
            $"SELECT id FROM \"public\".\"def_always__{PgPartBy.Day.Fmt(weekLater)}\";", cancellationToken: ct);
        Assert.NotEqual(0, leafId);
    }
}
