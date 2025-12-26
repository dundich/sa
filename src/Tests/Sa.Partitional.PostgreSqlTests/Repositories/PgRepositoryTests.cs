using Sa.Classes;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Repositories;


public class PgRepositoryTests(PgRepositoryTests.Fixture fixture) : IClassFixture<PgRepositoryTests.Fixture>
{
    const string PartPostfix = "part$";

    public class Fixture : PgDataSourceFixture<IPartRepository>
    {
        public Fixture()
        {
            Services.AddPartitional((_, builder) =>
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
        PartByRangeInfo item = list.First(c => c.Id == "public.\"test_10__1__some1__some2__y2024m12d03\"");
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
}

