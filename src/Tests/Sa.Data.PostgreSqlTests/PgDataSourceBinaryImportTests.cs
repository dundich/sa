using Npgsql;
using NpgsqlTypes;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;
using System.Linq;

namespace Sa.Data.PostgreSqlTests;

[Collection(nameof(PgDataSourceFixture))]
public class PgDataSourceBinaryImportTests(PgDataSourceFixture fixture) : IClassFixture<PgDataSourceFixture>
{
    [Fact()]
    public async Task BeginBinaryImport_ImportsRows()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS binary_import (id int, name text, active bool);
            DELETE FROM binary_import;
            """, TestContext.Current.CancellationToken);

        var imported = await fixture.DataSource.BeginBinaryImport(
            "COPY binary_import (id, name, active) FROM STDIN BINARY",
            async (writer, ct) =>
            {
                for (int i = 1; i <= 50; i++)
                {
                    writer.StartRow();
                    writer.Write(i, NpgsqlDbType.Integer);
                    writer.Write($"item{i}", NpgsqlDbType.Varchar);
                    writer.Write(i % 2 == 0, NpgsqlDbType.Boolean);
                }
                return await writer.CompleteAsync(ct);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(50UL, imported);

        var count = (int)(long)(await fixture.DataSource.ExecuteScalar("SELECT COUNT(*) FROM binary_import", TestContext.Current.CancellationToken))!;
        Assert.Equal(50, count);

        // verify data integrity
        var first = await fixture.DataSource.ExecuteReaderFirst<string>(
            "SELECT name FROM binary_import ORDER BY id LIMIT 1",
            TestContext.Current.CancellationToken);
        Assert.Equal("item1", first);

        await fixture.DataSource.ExecuteNonQuery("DROP TABLE IF EXISTS binary_import;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task BeginBinaryImport_EmptyImport_ReturnsZero()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS binary_import_empty (id int);
            DELETE FROM binary_import_empty;
            """, TestContext.Current.CancellationToken);

        var imported = await fixture.DataSource.BeginBinaryImport(
            "COPY binary_import_empty (id) FROM STDIN BINARY",
            async (writer, ct) =>
            {
                return await writer.CompleteAsync(ct);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0UL, imported);

        await fixture.DataSource.ExecuteNonQuery("DROP TABLE IF EXISTS binary_import_empty;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task BeginBinaryImport_GuidAndTimestamp()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS binary_import_types (
                guid_val uuid,
                ts_val timestamptz,
                payload bytea
            );
            DELETE FROM binary_import_types;
            """, TestContext.Current.CancellationToken);

        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var ts = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var imported = await fixture.DataSource.BeginBinaryImport(
            "COPY binary_import_types (guid_val, ts_val, payload) FROM STDIN BINARY",
            async (writer, ct) =>
            {
                writer.StartRow();
                writer.Write(guid1, NpgsqlDbType.Uuid);
                writer.Write(ts.ToUnixTimeMilliseconds(), NpgsqlDbType.Timestamp);
                writer.Write(payload, NpgsqlDbType.Bytea);

                writer.StartRow();
                writer.Write(guid2, NpgsqlDbType.Uuid);
                writer.Write(ts.AddMinutes(1).ToUnixTimeMilliseconds(), NpgsqlDbType.Timestamp);
                writer.Write(payload.Reverse().ToArray(), NpgsqlDbType.Bytea);

                return await writer.CompleteAsync(ct);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2UL, imported);

        var count = (int)(long)(await fixture.DataSource.ExecuteScalar("SELECT COUNT(*) FROM binary_import_types", TestContext.Current.CancellationToken))!;
        Assert.Equal(2, count);

        await fixture.DataSource.ExecuteNonQuery("DROP TABLE IF EXISTS binary_import_types;", TestContext.Current.CancellationToken);
    }

    [Fact()]
    public async Task BeginBinaryImport_LargeBatch()
    {
        await fixture.DataSource.ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS binary_import_large (seq int, data text);
            DELETE FROM binary_import_large;
            """, TestContext.Current.CancellationToken);

        const int batchSize = 1000;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var imported = await fixture.DataSource.BeginBinaryImport(
            "COPY binary_import_large (seq, data) FROM STDIN BINARY",
            async (writer, ct) =>
            {
                for (int i = 0; i < batchSize; i++)
                {
                    writer.StartRow();
                    writer.Write(i, NpgsqlDbType.Integer);
                    writer.Write($"data-{i}-{new string('x', 100)}", NpgsqlDbType.Text);
                }
                return await writer.CompleteAsync(ct);
            },
            TestContext.Current.CancellationToken);

        sw.Stop();

        Assert.Equal(batchSize, (int)imported);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Large batch took too long: {sw.ElapsedMilliseconds}ms");

        await fixture.DataSource.ExecuteNonQuery("DROP TABLE IF EXISTS binary_import_large;", TestContext.Current.CancellationToken);
    }
}
