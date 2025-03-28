using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests;


public class PartitionManagerTests(PartitionManagerTests.Fixture fixture) : IClassFixture<PartitionManagerTests.Fixture>
{
    public class Fixture : PgDataSourceFixture<IPartitionManager>
    {
        public Fixture()
        {
            Services.AddPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("test_41",
                        "id INT NOT NULL",
                        "gender TEXT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "payload_id TEXT NOT NULL"
                    )
                    .WithPartSeparator("_")
                    .PartByList("tenant_id", "gender")
                    .AddMigration(1, ["male", "female"])
                    .AddMigration(2, "male")
                    .AddMigration(2, "female")
                    .PartByRange(PgPartBy.Day, "created_at")
                    ;

                    schema.AddTable("test_empty",
                        "id INT NOT NULL"
                    )
                    .AddMigration()
                    // double check
                    .AddMigration()
                    ;
                });
            })
            .AddDataSource(configure => configure.WithConnectionString(_ => this.ConnectionString))
            ;
        }
    }


    private IPartitionManager Sub => fixture.Sub;

    [Fact]
    public async Task MigrateTest()
    {
        Console.WriteLine(fixture.ConnectionString);
        // Act
        int i = await Sub.Migrate(TestContext.Current.CancellationToken);
        Assert.True(i > 0);
    }


    [Fact]
    public async Task Migrate_WithDates()
    {

        DateTimeOffset today = DateTimeOffset.Now.AddDays(7);
        DateTimeOffset tomorrow = today.AddDays(1);

        // Act
        int i = await Sub.Migrate([today, tomorrow], TestContext.Current.CancellationToken);

        Assert.NotEqual(0, i);

        string postfix = PgPartBy.Day.Fmt(tomorrow);

        string table = $"public.test_41_1_male_{postfix}";

        await fixture.CheckTable(table);

        Assert.True(true);
    }
}
