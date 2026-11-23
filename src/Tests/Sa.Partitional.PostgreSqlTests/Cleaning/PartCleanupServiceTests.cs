using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Cleaning;

public class PartCleanupServiceTests(PartCleanupServiceTests.Fixture fixture) : IClassFixture<PartCleanupServiceTests.Fixture>
{

    public class Fixture : PgDataSourceFixture<IPartCleanupService>
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
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
                    .AddMigrationChildren(1, "New York", "London")
                    .AddMigrationChildren(2, "Moscow", "Kazan", "Yekaterinburg")
                    ;

                });
            })
            .AddDataSource(configure => configure.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)))
            .AddPartCleanupSchedule()
            ;
        }
    }


    private IPartCleanupService Sub => fixture.Sub;

    private Task<List<PartByRangeInfo>> GetParts()
        => GetPartRep().GetPartsFromDate("test_70", DateTimeOffset.Now);

    private IPartRepository GetPartRep() => fixture.ServiceProvider.GetRequiredService<IPartRepository>();

    private async Task MigrateTest()
    {
        await GetPartRep().MigrateAsync([DateTimeOffset.Now], CancellationToken.None);
    }

    [Fact]
    public async Task CleanTest()
    {
        await MigrateTest();

        List<PartByRangeInfo> list = await GetParts();
        Assert.NotEmpty(list);

        // Derived instead of the old hard-coded 5, which silently re-encoded the fixture's 2+3
        // branches: one leaf per declared branch for the migrated date.
        int branches = (await DeclaredBranches()).Length;
        Assert.Equal(branches, list.Count);

        DateTimeOffset toDate = DateTimeOffset.UtcNow.AddDays(1);

        int i = await Sub.CleanAsync(toDate, CancellationToken.None);
        Assert.Equal(branches, i);

        int expected = list.Count - i;

        list = await GetParts();
        Assert.Equal(expected, list.Count);
    }

    /// <summary>The branches the fixture's single table declares through AddMigration.</summary>
    private async Task<StrOrNum[][]> DeclaredBranches()
        => await fixture.ServiceProvider
            .GetRequiredService<ISettingsBuilder>()
            .Build()
            .Tables
            .Single()
            .Migration
            .GetPartsAsync(CancellationToken.None);
}
