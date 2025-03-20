using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests;


public class PartitionIndexTests(PartitionIndexTests.Fixture fixture) : IClassFixture<PartitionIndexTests.Fixture>
{
    public class Fixture : PgDataSourceFixture<IPartitionManager>
    {
        public Fixture()
        {
            Services.AddPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("test_61",
                        "id INT NOT NULL"
                    )
                    ;

                });
            })
            .AddDataSource(configure => configure.WithConnectionString(_ => this.ConnectionString))
            ;
        }
    }


    private IPartitionManager Sub => fixture.Sub;



    [Fact]
    public async Task InsertingDoubleParts()
    {
        Console.WriteLine(fixture.ConnectionString, TestContext.Current.CancellationToken);

        DateTimeOffset today = DateTimeOffset.Now;
        DateTimeOffset tomorrow = today.AddDays(1);

        // Act
        int i = await Sub.Migrate([today, tomorrow], TestContext.Current.CancellationToken);

        Assert.Equal(2, i);

        long unixTime = today.ToUnixTimeSeconds();

        i = await fixture.DataSource.ExecuteNonQuery(
$"""
INSERT INTO test_61 
    (id,created_at)
VALUES
    (1,{unixTime}),
    (1,{unixTime + 1})
ON CONFLICT DO NOTHING
""", TestContext.Current.CancellationToken);

        Assert.Equal(2, i);
    }
}
