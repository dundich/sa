using Sa.Partitional.PostgreSql.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSqlTests.Repositories;


public class PgRepositoryTests(PgRepositoryTests.Fixture fixture) : IClassFixture<PgRepositoryTests.Fixture>
{
    const string PartPostfix = "part$";

    public class Fixture : PgDataSourceFixture<IPartRepository>
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("test_10",
                        "id INT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "part_1 TEXT NOT NULL",
                        "payload_id TEXT"
                     )
                     .PartByList("tenant_id", "part", "part_1")
                     .TimestampAs("date")
                     .WithPartTablePostfix(PartPostfix)
                    ;

                    schema.AddTable("test_11",
                        "id INT NOT NULL",
                        "part_str TEXT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "payload_id TEXT NOT NULL"
                    )
                    .PartByList("part_str", "tenant_id")
                    ;

                    schema.AddTable("test_12",
                        "id INT NOT NULL"
                    )
                    ;

                });
            })
            .AddDataSource(configure => configure.WithConnectionString(_ => this.ConnectionString))
            ;
        }
    }



    [Fact()]
    public async Task CreatePartTest()
    {
        var sql = $"SELECT count(*) FROM public.\"test_11__{PartPostfix}\";";
        Console.WriteLine(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await fixture.Sub.CreatePart("test_11", DateTimeOffset.Now, ["some", 12], TestContext.Current.CancellationToken);
        int i = await fixture.DataSource.ExecuteReaderFirst<int>(sql, TestContext.Current.CancellationToken);
        Assert.True(i > 0);
    }

    [Fact()]
    public async Task CreatePart_WithEmptyListTest()
    {
        var sql = $"SELECT count(*) FROM public.\"test_12__{PartPostfix}\";";
        await fixture.Sub.CreatePart("test_12", DateTimeOffset.Now, [], TestContext.Current.CancellationToken);
        int i = await fixture.DataSource.ExecuteReaderFirst<int>(sql, TestContext.Current.CancellationToken);
        Assert.True(i > 0);
    }


    [Fact()]
    public async Task GetPartByRangeListTest()
    {
        var timeExpected = new DateTimeOffset(2024, 12, 03, 00, 00, 00, TimeSpan.Zero);

        await fixture.Sub.CreatePart(
            "test_10",
            timeExpected.AddMinutes(22).AddHours(12),
            [1, "some1", "some2"],
            TestContext.Current.CancellationToken);

        IReadOnlyCollection<PartByRangeInfo> list = await fixture.Sub.GetPartsFromDate(
            "test_10",
            timeExpected.AddDays(-3),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(list);
        PartByRangeInfo item = list.First(c => c.Id == "\"public\".\"test_10__1__some1__some2__y2024m12d03\"");
        Assert.NotNull(item);
        Assert.Equal(timeExpected, item.FromDate);
        Assert.Equal("public.test_10", item.RootTableName);
    }

    [Fact()]
    public async Task MigrateTest()
    {
        int i = await fixture.Sub.Migrate([DateTime.Now, DateTime.Now.AddDays(1)], table =>
        {
            StrOrNum[][] result = table switch
            {
                "public.test_10" => [[1, "part1", "part2"], [2, "part1", "part2"]],
                "public.test_11" => [["part1", 1], ["part1", 2], ["part2", 1]],
                "public.test_12" => [],
                _ => throw new NotImplementedException(),
            };

            return Task.FromResult(result);
        }, TestContext.Current.CancellationToken);

        Assert.True(i > 0);
    }

    [Fact]
    public async Task CreatePart_Concurrently_ForTheSameValues_LeavesOnePartitionAndOneCacheRow()
    {
        DateTimeOffset date = new(2025, 3, 7, 10, 0, 0, TimeSpan.Zero);

        // Both callers race on a cold cache: the cache table and the partition may be created twice,
        // and both CREATE TABLE statements reach PostgreSQL. IF NOT EXISTS plus ON CONFLICT DO
        // NOTHING make the second one a no-op instead of a duplicate-key error.
        await Task.WhenAll(
            fixture.Sub.CreatePart("test_10", date, [7, "race1", "race1"], TestContext.Current.CancellationToken),
            fixture.Sub.CreatePart("test_10", date, [7, "race1", "race1"], TestContext.Current.CancellationToken));

        int partitions = await fixture.DataSource.ExecuteReaderFirst<int>(
            "SELECT count(*) FROM pg_class WHERE relname = 'test_10__7__race1__race1__y2025m03d07';",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, partitions);

        int cacheRows = await fixture.DataSource.ExecuteReaderFirst<int>(
            $"""SELECT count(*) FROM public."test_10__{PartPostfix}" WHERE id LIKE '%7__race1__race1%';""",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, cacheRows);
    }

    [Fact]
    public async Task ExecuteDDL_AfterDispose_FailsAtTheLock()
    {
        PartRepository repository = new(
            fixture.DataSource,
            fixture.ServiceProvider.GetRequiredService<ISqlBuilder>(),
            NullLogger<PartRepository>.Instance);
        repository.Dispose();

        // A disposed repository is a closed door rather than a broken one: the DDL semaphore is gone,
        // so the call fails on the lock instead of running half of a migration and then failing.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => repository.ExecuteDDL("SELECT 1;", TestContext.Current.CancellationToken));
    }
}
