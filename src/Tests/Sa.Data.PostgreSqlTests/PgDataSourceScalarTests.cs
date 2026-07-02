using Npgsql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Data.PostgreSqlTests;

[Collection(nameof(PgDataSourceFixture))]
public class PgDataSourceScalarTests(PgDataSourceFixture fixture) : IClassFixture<PgDataSourceFixture>
{
    [Fact()]
    public async Task ExecuteScalar_ReturnsFirstValue()
    {
        var actual = await fixture.DataSource.ExecuteScalar("SELECT 42", TestContext.Current.CancellationToken);
        Assert.Equal(42, actual);
    }

    [Fact()]
    public async Task ExecuteScalar_ReturnsNull_ForEmptyResult()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS empty_table (id int);
            DELETE FROM empty_table;
            """, TestContext.Current.CancellationToken);

        var actual = await fixture.DataSource.ExecuteScalar("SELECT id FROM empty_table", TestContext.Current.CancellationToken);
        Assert.Null(actual);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_ReturnsInt()
    {
        var actual = await fixture.DataSource.ExecuteScalarTyped<int>("SELECT 123", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(123, actual);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_ReturnsString()
    {
        const string expected = "hello";
        var actual = await fixture.DataSource.ExecuteScalarTyped<string>("SELECT 'hello'", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_ReturnsGuid()
    {
        var expected = Guid.NewGuid();
        var actual = await fixture.DataSource.ExecuteScalarTyped<Guid>($"SELECT '{expected}'::uuid", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_NumericAggregation()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            INSERT INTO empty_table (id) VALUES (1), (2), (3);
            """, cancellationToken: TestContext.Current.CancellationToken);

        var sum = await fixture.DataSource.ExecuteScalarTyped<long>("SELECT SUM(id)::bigint FROM empty_table", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(6L, sum);

        // cleanup
        await fixture.DataSource.ExecuteNonQuery("DELETE FROM empty_table;", cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_WithParameters()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS param_test_table (name text, value int);
            DELETE FROM param_test_table;
            """, cancellationToken: TestContext.Current.CancellationToken);

        var val = await fixture.DataSource.ExecuteScalarTyped<int>(
            """INSERT INTO param_test_table (name, value) VALUES (@p0, @p1) RETURNING value""", 
            [new NpgsqlParameter { ParameterName = "p0", Value = "test" }, new NpgsqlParameter { ParameterName = "p1", Value = 99 }]
            , cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(99, val);

        await fixture.DataSource.ExecuteNonQuery("DELETE FROM param_test_table;", cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_Bool()
    {
        var actual = await fixture.DataSource.ExecuteScalarTyped<bool>("SELECT true", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(actual);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_Double()
    {
        var actual = await fixture.DataSource.ExecuteScalarTyped<double>("SELECT 3.14", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Math.Abs(actual - 3.14) < 0.001);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_DateTime()
    {
        var expected = DateTime.UtcNow.Date;
        var actual = await fixture.DataSource.ExecuteScalarTyped<DateTime>("SELECT CURRENT_DATE", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected.Date, actual.Date);
    }

    [Fact()]
    public async Task ExecuteScalar_Typed_DefaultForEmpty()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS empty_scalar (val int);
            DELETE FROM empty_scalar;
            """, cancellationToken: TestContext.Current.CancellationToken);

        var actual = await fixture.DataSource.ExecuteScalar("SELECT val FROM empty_scalar WHERE val > 0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(actual);

        await fixture.DataSource.ExecuteNonQuery("DROP TABLE IF EXISTS empty_scalar;", cancellationToken: TestContext.Current.CancellationToken);
    }
}
