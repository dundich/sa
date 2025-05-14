using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Extensions;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Cleaning;

public class PartCleanupServiceTests(PartCleanupServiceTests.Fixture fixture) : IClassFixture<PartCleanupServiceTests.Fixture>
{

    public class Fixture : PgDataSourceFixture<IPartCleanupService>
    {
        public Fixture()
        {
            Services.AddPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("test_70",
                        "id INT NOT NULL",
                        "city TEXT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "payload_id TEXT NOT NULL"
                    )
                    .PartByList("tenant_id", "city")
                    .AddMigration(1, ["New York", "London"])
                    .AddMigration(2, ["Moscow", "Kazan", "Yekaterinburg"])
                    ;

                });
            })
            .AddDataSource(configure => configure.WithConnectionString(_ => this.ConnectionString))
            .AddPartCleanupSchedule()
            ;
        }
    }


    private IPartCleanupService Sub => fixture.Sub;

    private Task<List<PartByRangeInfo>> GetParts()
        => GetPartRep().GetPartsFromDate("test_70", DateTimeOffset.Now.StartOfDay());

    private IPartRepository GetPartRep() => fixture.ServiceProvider.GetRequiredService<IPartRepository>();

    private async Task MigrateTest()
    {
        await GetPartRep().Migrate([DateTimeOffset.Now], CancellationToken.None);
    }

    [Fact()]
    public async Task CleanTest()
    {
        Console.WriteLine(fixture.ConnectionString);

        await MigrateTest();

        List<PartByRangeInfo> list = await GetParts();
        Assert.NotEmpty(list);

        DateTimeOffset toDate = DateTimeOffset.UtcNow.AddDays(1);

        int i = await Sub.Clean(toDate, CancellationToken.None);
        Assert.Equal(5, i);

        int expected = list.Count - i;

        list = await GetParts();
        Assert.Equal(expected, list.Count);
    }
}
