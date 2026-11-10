using Npgsql;
using NpgsqlTypes;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Data.PostgreSqlTests;

[Collection(nameof(PgDataSourceFixture))]
public class PgDataSourceReaderFirstTests(PgDataSourceFixture fixture) : IClassFixture<PgDataSourceFixture>
{
    [Fact()]
    public async Task ExecuteReaderFirst_Int()
    {
        await fixture.DataSource.ExecuteNonQueryAsync("""
            CREATE TABLE IF NOT EXISTS reader_first_int (val int);
            DELETE FROM reader_first_int;
            INSERT INTO reader_first_int (val) VALUES (10), (20), (30);
            """, TestContext.Current.CancellationToken);

        var first = await fixture.DataSource.ExecuteReaderFirstAsync<int>(
            "SELECT val FROM reader_first_int ORDER BY val LIMIT 1",
            TestContext.Current.CancellationToken);

        Assert.Equal(10, first);

        await fixture.DataSource.ExecuteNonQueryAsync("DROP TABLE IF EXISTS reader_first_int;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_String()
    {
        await fixture.DataSource.ExecuteNonQueryAsync("""
            CREATE TABLE IF NOT EXISTS reader_first_str (name text);
            DELETE FROM reader_first_str;
            INSERT INTO reader_first_str (name) VALUES ('Zebra'), ('Apple'), ('Mango');
            """, TestContext.Current.CancellationToken);

        var first = await fixture.DataSource.ExecuteReaderFirstAsync<string>(
            "SELECT name FROM reader_first_str ORDER BY name LIMIT 1",
            TestContext.Current.CancellationToken);

        Assert.Equal("Apple", first);

        await fixture.DataSource.ExecuteNonQueryAsync("DROP TABLE IF EXISTS reader_first_str;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_Guid()
    {
        var expected = Guid.NewGuid();

        await fixture.DataSource.ExecuteNonQueryAsync("""
            CREATE TABLE IF NOT EXISTS reader_first_guid (guid_val uuid);
            DELETE FROM reader_first_guid;
            INSERT INTO reader_first_guid (guid_val) VALUES (@g);
            """,
            [new NpgsqlParameter { ParameterName = "g", Value = expected, NpgsqlDbType = NpgsqlDbType.Uuid }],
            TestContext.Current.CancellationToken);

        var actual = await fixture.DataSource.ExecuteReaderFirstAsync<Guid>(
            "SELECT guid_val FROM reader_first_guid",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);

        await fixture.DataSource.ExecuteNonQueryAsync("DROP TABLE IF EXISTS reader_first_guid;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_WithParameters()
    {
        await fixture.DataSource.ExecuteNonQueryAsync("""
            CREATE TABLE IF NOT EXISTS reader_first_param (name text, age int);
            DELETE FROM reader_first_param;
            INSERT INTO reader_first_param (name, age) VALUES ('Tom', 18), ('Jerry', 5);
            """, TestContext.Current.CancellationToken);

        var age = await fixture.DataSource.ExecuteReaderFirstAsync<int>(
            "SELECT age FROM reader_first_param WHERE name = @name",
            [new NpgsqlParameter { ParameterName = "name", Value = "Tom" }],
            TestContext.Current.CancellationToken);

        Assert.Equal(18, age);

        await fixture.DataSource.ExecuteNonQueryAsync("DROP TABLE IF EXISTS reader_first_param;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_Bool()
    {
        await fixture.DataSource.ExecuteNonQueryAsync("""
            CREATE TABLE IF NOT EXISTS reader_first_bool (flag bool);
            DELETE FROM reader_first_bool;
            INSERT INTO reader_first_bool (flag) VALUES (true);
            """, TestContext.Current.CancellationToken);

        var flag = await fixture.DataSource.ExecuteReaderFirstAsync<bool>(
            "SELECT flag FROM reader_first_bool",
            TestContext.Current.CancellationToken);

        Assert.True(flag);

        await fixture.DataSource.ExecuteNonQueryAsync("DROP TABLE IF EXISTS reader_first_bool;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_Double()
    {
        var expected = 2.71828;
        var actual = await fixture.DataSource.ExecuteReaderFirstAsync<double>(
            "SELECT 2.71828",
            TestContext.Current.CancellationToken);

        Assert.True(Math.Abs(actual - expected) < 0.001);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_Char()
    {
        var actual = await fixture.DataSource.ExecuteReaderFirstAsync<char>(
            "SELECT 'A'::char",
            TestContext.Current.CancellationToken);

        Assert.Equal('A', actual);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_Decimal()
    {
        var expected = 19.99m;
        var actual = await fixture.DataSource.ExecuteReaderFirstAsync<decimal>(
            "SELECT 19.99::numeric(10,2)",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_CountReturnsZeroWhenEmpty()
    {
        await fixture.DataSource.ExecuteNonQueryAsync("""
            CREATE TABLE IF NOT EXISTS reader_first_empty (val int);
            DELETE FROM reader_first_empty;
            """, TestContext.Current.CancellationToken);

        var count = await fixture.DataSource.ExecuteReaderFirstAsync<int>(
            "SELECT COUNT(*) FROM reader_first_empty",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, count);

        await fixture.DataSource.ExecuteNonQueryAsync("DROP TABLE IF EXISTS reader_first_empty;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_AggregationFunction()
    {
        await fixture.DataSource.ExecuteNonQueryAsync("""
            CREATE TABLE IF NOT EXISTS reader_first_agg (val int);
            DELETE FROM reader_first_agg;
            INSERT INTO reader_first_agg (val) VALUES (10), (20), (30);
            """, TestContext.Current.CancellationToken);

        var avg = await fixture.DataSource.ExecuteReaderFirstAsync<double>(
            "SELECT AVG(val)::double precision FROM reader_first_agg",
            TestContext.Current.CancellationToken);

        Assert.True(Math.Abs(avg - 20.0) < 0.001);

        await fixture.DataSource.ExecuteNonQueryAsync("DROP TABLE IF EXISTS reader_first_agg;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_MultiRow_ReturnsFirstNotLast()
    {
        await fixture.DataSource.ExecuteNonQueryAsync("""
            CREATE TABLE IF NOT EXISTS reader_first_order (val int);
            DELETE FROM reader_first_order;
            INSERT INTO reader_first_order (val) VALUES (10), (20), (30);
            """, TestContext.Current.CancellationToken);

        // No LIMIT on purpose: the method itself must return the first row, not the last.
        var first = await fixture.DataSource.ExecuteReaderFirstAsync<int>(
            "SELECT val FROM reader_first_order ORDER BY val",
            TestContext.Current.CancellationToken);

        Assert.Equal(10, first);

        await fixture.DataSource.ExecuteNonQueryAsync("DROP TABLE IF EXISTS reader_first_order;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_NullInt_ReturnsDefault()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<int>(
            "SELECT NULL::int",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, value);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_NullString_ReturnsNull()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<string>(
            "SELECT NULL::text",
            TestContext.Current.CancellationToken);

        Assert.Null(value);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_NullableInt_ReadsValue()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<int?>(
            "SELECT 42",
            TestContext.Current.CancellationToken);

        Assert.Equal(42, value);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_NullableInt_Null_ReturnsNull()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<int?>(
            "SELECT NULL::int",
            TestContext.Current.CancellationToken);

        Assert.False(value.HasValue);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_NullableLong_ReadsValue()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<long?>(
            "SELECT 9223372036854775806",
            TestContext.Current.CancellationToken);

        Assert.Equal(9223372036854775806L, value);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_Float()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<float>(
            "SELECT 1.5::real",
            TestContext.Current.CancellationToken);

        Assert.Equal(1.5f, value);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_Enum()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<SampleStatus>(
            "SELECT 2",
            TestContext.Current.CancellationToken);

        Assert.Equal(SampleStatus.Two, value);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_NullableEnum_ReadsValue()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<SampleStatus?>(
            "SELECT 1",
            TestContext.Current.CancellationToken);

        Assert.Equal(SampleStatus.One, value);
    }

    [Fact()]
    public async Task ExecuteReaderFirst_TimeSpan()
    {
        var value = await fixture.DataSource.ExecuteReaderFirstAsync<TimeSpan>(
            "SELECT INTERVAL '1 hour 30 minutes'",
            TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMinutes(90), value);
    }

    private enum SampleStatus
    {
        Zero = 0,
        One = 1,
        Two = 2,
    }
}
