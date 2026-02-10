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

                });
            })
            .AddDataSource(configure => configure.WithConnectionString(_ => this.ConnectionString))
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
    public async Task InCache_EnsureCache_ReturnsTrue()
    {
        DateTimeOffset date = DateTimeOffset.Now;
        StrOrNum[] parValues = [1, "some1"];

        // Act
        bool result = await PartCache.InCache("test_22", date, parValues, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result);

        result = await PartCache.EnsureCache("test_22", date, parValues, TestContext.Current.CancellationToken);

        Assert.True(result);

        result = await PartCache.InCache("test_22", date, parValues, TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task InCache_RemoveCache()
    {
        DateTimeOffset date = DateTimeOffset.Now;

        bool result = await PartCache.EnsureCache("test_23", date, [], TestContext.Current.CancellationToken);

        Assert.True(result);

        await PartCache.RemoveCache("test_23", TestContext.Current.CancellationToken);

        result = await PartCache.InCache("test_23", date, [], TestContext.Current.CancellationToken);

        Assert.True(result);
    }


    [Fact]
    public async Task InCache_EnsureCache_DifferentParts()
    {
        DateTimeOffset date = DateTimeOffset.Now;
        StrOrNum[] partValues_1 = [1, "some1"];
        StrOrNum[] partValues_2 = [2, "some1"];

        var result = await PartCache.EnsureCache("test_24", date, partValues_1, TestContext.Current.CancellationToken);

        Assert.True(result);

        result = await PartCache.InCache("test_24", date, partValues_2, TestContext.Current.CancellationToken);

        Assert.False(result);

        result = await PartCache.EnsureCache("test_24", date, partValues_2, TestContext.Current.CancellationToken);

        Assert.True(result);
    }
}
