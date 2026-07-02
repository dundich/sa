using Npgsql;
using NpgsqlTypes;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;

namespace Sa.Data.PostgreSqlTests;

[Collection(nameof(PgDataSourceFixture))]
public class PgDataSourceReaderListTests(PgDataSourceFixture fixture) : IClassFixture<PgDataSourceFixture>
{
    [Fact()]
    public async Task ExecuteReaderList_ReturnsAllRows()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS reader_list_test (id int, name text);
            DELETE FROM reader_list_test;
            INSERT INTO reader_list_test (id, name) VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Charlie');
            """, TestContext.Current.CancellationToken);

        var names = await fixture.DataSource.ExecuteReaderList<string>(
            "SELECT name FROM reader_list_test ORDER BY id",
            reader => reader.GetString(0),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, names.Count);
        Assert.Contains("Alice", names);
        Assert.Contains("Bob", names);
        Assert.Contains("Charlie", names);

        await fixture.DataSource.ExecuteNonQuery("DELETE FROM reader_list_test;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderList_WithParameters()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS reader_list_params (name text, active bool);
            DELETE FROM reader_list_params;
            INSERT INTO reader_list_params (name, active) VALUES ('Alice', true), ('Bob', false), ('Charlie', true);
            """, TestContext.Current.CancellationToken);

        var activeNames = await fixture.DataSource.ExecuteReaderList<string>(
            "SELECT name FROM reader_list_params WHERE active = @active ORDER BY name",
            reader => reader.GetString(0),
            [new NpgsqlParameter { ParameterName = "active", Value = true }],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, activeNames.Count);
        Assert.Contains("Alice", activeNames);
        Assert.Contains("Charlie", activeNames);

        await fixture.DataSource.ExecuteNonQuery("DELETE FROM reader_list_params;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderList_ReturnsEmpty_ForNoData()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS reader_list_empty (id int);
            DELETE FROM reader_list_empty;
            """, TestContext.Current.CancellationToken);

        var result = await fixture.DataSource.ExecuteReaderList<int>(
            "SELECT id FROM reader_list_empty",
            reader => reader.GetInt32(0),
            TestContext.Current.CancellationToken);

        Assert.Empty(result);

        await fixture.DataSource.ExecuteNonQuery("DROP TABLE IF EXISTS reader_list_empty;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderList_TupleProjection()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS reader_list_tuple (id int, name text);
            DELETE FROM reader_list_tuple;
            INSERT INTO reader_list_tuple (id, name) VALUES (1, 'First'), (2, 'Second');
            """, TestContext.Current.CancellationToken);

        var tuples = await fixture.DataSource.ExecuteReaderList<(int Id, string Name)>(
            "SELECT id, name FROM reader_list_tuple ORDER BY id",
            reader => (reader.GetInt32(0), reader.GetString(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, tuples.Count);
        Assert.Equal(1, tuples[0].Id);
        Assert.Equal("First", tuples[0].Name);
        Assert.Equal(2, tuples[1].Id);
        Assert.Equal("Second", tuples[1].Name);

        await fixture.DataSource.ExecuteNonQuery("DELETE FROM reader_list_tuple;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderList_Guid()
    {
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();

        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS reader_list_guid (guid_val uuid);
            DELETE FROM reader_list_guid;
            INSERT INTO reader_list_guid (guid_val) VALUES (@g1), (@g2);
            """,
            [new NpgsqlParameter { ParameterName = "g1", Value = guid1, NpgsqlDbType = NpgsqlDbType.Uuid },
             new NpgsqlParameter { ParameterName = "g2", Value = guid2, NpgsqlDbType = NpgsqlDbType.Uuid }],
            TestContext.Current.CancellationToken);

        var guids = await fixture.DataSource.ExecuteReaderList<Guid>(
            "SELECT guid_val FROM reader_list_guid ORDER BY guid_val",
            reader => reader.GetFieldValue<Guid>(0),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, guids.Count);
        Assert.Contains(guid1, guids);
        Assert.Contains(guid2, guids);

        await fixture.DataSource.ExecuteNonQuery("DROP TABLE IF EXISTS reader_list_guid;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task ExecuteReaderList_CountMatchesRowCount()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS reader_list_count (val int);
            DELETE FROM reader_list_count;
            INSERT INTO reader_list_count (val) SELECT generate_series(1, 100);
            """, TestContext.Current.CancellationToken);

        var count = await fixture.DataSource.ExecuteReaderList<int>(
            "SELECT val FROM reader_list_count ORDER BY val",
            reader => reader.GetInt32(0),
            TestContext.Current.CancellationToken);

        Assert.Equal(100, count.Count);

        await fixture.DataSource.ExecuteNonQuery("DROP TABLE IF EXISTS reader_list_count;", TestContext.Current.CancellationToken);
    }
}
