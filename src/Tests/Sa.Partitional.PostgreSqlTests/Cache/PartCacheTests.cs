using Microsoft.Extensions.DependencyInjection;
using Sa.Classes;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Cache;

namespace Sa.Partitional.PostgreSqlTests.Cache;


public class PartCacheTests(PartCacheTests.Fixture fixture) : IClassFixture<PartCacheTests.Fixture>
{

    public class Fixture : PgDataSourceFixture<IPartCache>
    {
        public Fixture()
        {
            Services.AddPartitional((_, builder) =>
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

        public IPartRepository PartRepository => ServiceProvider.GetRequiredService<IPartRepository>();
    }


    private IPartCache Sub => fixture.Sub;


    [Fact]
    public async Task InCache_TableNotExists_ReturnsFalse()
    {
        Console.WriteLine(fixture.ConnectionString);

        bool actual = await Sub.InCache("different_table", DateTimeOffset.Now, ["p1", 145]);
        // Assert
        Assert.False(actual);
    }

    [Fact]
    public async Task InCache_PartExistsInCache_ReturnsTrue()
    {
        var date = DateTimeOffset.Now;

        await fixture.PartRepository.CreatePart("test_20", date, [1, "some"]);

        // Act
        bool result = await Sub.InCache("test_20", date, [1, "some"]);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task InCache_DateNotInCache_ReturnsFalse()
    {
        await fixture.PartRepository.CreatePart("test_21", DateTimeOffset.Now, [1, "some"]);

        // Act
        bool result = await Sub.InCache("test_21", DateTimeOffset.Now.AddDays(1), [1, "some"]);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task InCache_EnsureCache_ReturnsTrue()
    {
        DateTimeOffset date = DateTimeOffset.Now;
        StrOrNum[] parValues = [1, "some1"];

        // Act
        bool result = await Sub.InCache("test_22", date, parValues);

        // Assert
        Assert.False(result);

        result = await Sub.EnsureCache("test_22", date, parValues);

        Assert.True(result);

        result = await Sub.InCache("test_22", date, parValues);

        Assert.True(result);
    }

    [Fact]
    public async Task InCache_RemoveCache()
    {
        DateTimeOffset date = DateTimeOffset.Now;

        bool result = await Sub.EnsureCache("test_23", date, []);

        Assert.True(result);

        await Sub.RemoveCache("test_23");

        result = await Sub.InCache("test_23", date, []);

        Assert.True(result);
    }


    [Fact]
    public async Task InCache_EnsureCache_DifferentParts()
    {
        DateTimeOffset date = DateTimeOffset.Now;
        StrOrNum[] partValues_1 = [1, "some1"];
        StrOrNum[] partValues_2 = [2, "some1"];

        var result = await Sub.EnsureCache("test_24", date, partValues_1);

        Assert.True(result);

        result = await Sub.InCache("test_24", date, partValues_2);

        Assert.False(result);

        result = await Sub.EnsureCache("test_24", date, partValues_2);

        Assert.True(result);
    }
}
