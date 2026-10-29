using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Cache;
using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSqlTests.Cache;


public class PartCacheTests(PartCacheTests.Fixture fixture) : IClassFixture<PartCacheTests.Fixture>
{

    public class Fixture : PgDataSourceFixture<IPartRepository>
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("test_20",
                        "id INT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "payload_id TEXT"
                     )
                     .PartByList("tenant_id", "part")
                    ;

                    schema.AddTable("test_21",
                        "id INT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "payload_id TEXT"
                     )
                     .PartByList("tenant_id", "part")
                    ;

                    schema.AddTable("test_22",
                        "id INT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "payload_id TEXT"
                     )
                     .PartByList("tenant_id", "part")
                     ;


                    schema.AddTable("test_23",
                        "id INT NOT NULL"
                     )
                    ;

                    schema.AddTable("test_24",
                        "id INT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "payload_id TEXT"
                     )
                     .PartByList("tenant_id", "part")
                     ;

                    // Month-partitioned: the partition being written to started on the 1st, which is
                    // the case the one-day cache window used to miss.
                    schema.AddTable("test_25",
                        "id INT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "payload_id TEXT"
                     )
                     .PartByList("tenant_id", "part")
                     .PartByRange(PgPartBy.Month)
                     ;

                });
            })
            .AddDataSource(configure => configure.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)))
            ;
        }
    }


    private IPartCache PartCache => fixture.ServiceProvider.GetRequiredService<IPartCache>();


    [Fact]
    public async Task InCache_TableNotExists_ReturnsFalse()
    {
        Console.WriteLine(fixture.ConnectionString, TestContext.Current.CancellationToken);

        bool actual = await PartCache.InCache("different_table", DateTimeOffset.Now, ["p1", 145], TestContext.Current.CancellationToken);
        // Assert
        Assert.False(actual);
    }

    [Fact]
    public async Task InCache_PartExistsInCache_ReturnsTrue()
    {
        var date = DateTimeOffset.Now;

        await fixture.Sub.CreatePart("test_20", date, [1, "some"], TestContext.Current.CancellationToken);

        // Act
        bool result = await PartCache.InCache("test_20", date, [1, "some"], TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task InCache_DateNotInCache_ReturnsFalse()
    {
        await fixture.Sub.CreatePart("test_21", DateTimeOffset.Now, [1, "some"], TestContext.Current.CancellationToken);

        // Act
        bool result = await PartCache.InCache("test_21", DateTimeOffset.Now.AddDays(1), [1, "some"], TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task InCache_EnsureCache_MakesThePartitionVisible()
    {
        DateTimeOffset date = DateTimeOffset.Now;
        StrOrNum[] parValues = [1, "some1"];

        // Act
        bool result = await PartCache.InCache("test_22", date, parValues, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result);

        // EnsureCache reports by outcome: it returns when the partition exists, and creation is
        // verified through the cache rather than by a bool the call used to return.
        await PartCache.EnsureCache("test_22", date, parValues, TestContext.Current.CancellationToken);

        result = await PartCache.InCache("test_22", date, parValues, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task InCache_RemoveCache()
    {
        DateTimeOffset date = DateTimeOffset.Now;

        await PartCache.EnsureCache("test_23", date, [], TestContext.Current.CancellationToken);

        await PartCache.RemoveCache("test_23", TestContext.Current.CancellationToken);

        bool result = await PartCache.InCache("test_23", date, [], TestContext.Current.CancellationToken);

        // The drop of the snapshot is not a drop of the partition: the next read reloads it from the
        // cache table and finds it there.
        Assert.True(result);
    }


    [Fact]
    public async Task InCache_EnsureCache_DifferentParts()
    {
        DateTimeOffset date = DateTimeOffset.Now;
        StrOrNum[] partValues_1 = [1, "some1"];
        StrOrNum[] partValues_2 = [2, "some1"];

        await PartCache.EnsureCache("test_24", date, partValues_1, TestContext.Current.CancellationToken);

        var result = await PartCache.InCache("test_24", date, partValues_2, TestContext.Current.CancellationToken);

        Assert.False(result);

        await PartCache.EnsureCache("test_24", date, partValues_2, TestContext.Current.CancellationToken);

        result = await PartCache.InCache("test_24", date, partValues_2, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task InCache_MonthPartition_MatchesEveryDayOfTheMonth()
    {
        // UTC, like the partition range: a local "month" can differ from the UTC one around the 1st.
        DateTimeOffset date = DateTimeOffset.UtcNow;
        StrOrNum[] partValues = [1, "some"];

        await fixture.Sub.CreatePart("test_25", date, partValues, TestContext.Current.CancellationToken);

        // Any day of the current month belongs to the created month partition. Before the fix the
        // stored strategy resolved to Day, so `InCache` only matched on the 1st; the one-day snapshot
        // window on top of that meant a month partition was never visible at all.
        DateTimeOffset midMonth = new(date.Year, date.Month, 15, 12, 0, 0, TimeSpan.Zero);

        bool result = await PartCache.InCache("test_25", midMonth, partValues, TestContext.Current.CancellationToken);

        Assert.True(result);
    }
}
