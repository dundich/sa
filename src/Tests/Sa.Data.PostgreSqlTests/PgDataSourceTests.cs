using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Data.PostgreSqlTests;

public class PgDataSourceTests(PgDataSourceFixture fixture) : IClassFixture<PgDataSourceFixture>
{
    [Fact()]
    public async Task ExecuteNonQueryTest()
    {
        const int expected = -1;
        var actual = await fixture.DataSource.ExecuteNonQuery("SELECT 2", TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual);
    }

    [Fact()]
    public async Task ExecuteNonQueryWithParamsTest()
    {
        const int expected = 1;
        var actual = await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS users (
                name text,
                age int
            );

            INSERT INTO users (name, age) VALUES (@p1, @p2);
            """, [
                  new("p1", "Tom"),
                  new("p2", 18)
            ], TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual);
    }


    [Fact()]
    public async Task ExecuteReaderTest()
    {
        Console.WriteLine(fixture.ConnectionString);

        const int expected = 1;
        int actual = 0;

        await fixture.DataSource.ExecuteReader("SELECT 1", (reader, i) => actual = reader.GetInt32(0), TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }


    [Fact()]
    public async Task DiDataSourceTest()
    {
        ServiceCollection services = new();
        services.AddPgDataSource(b => b.WithConnectionString(fixture.ConnectionString));
        using var serviceProvider = services.BuildServiceProvider();

        IPgDataSource dataSource = serviceProvider.GetRequiredService<IPgDataSource>();

        const int expected = 1;
        int actual = 0;

        await dataSource.ExecuteReader("SELECT 1", (reader, i) => actual = reader.GetInt32(0), TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }
}
