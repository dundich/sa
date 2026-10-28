using Npgsql;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Postgres;
using Sa.Partitional.PostgreSql;
using System.Text;
using System.Text.RegularExpressions;

namespace Sa.HybridFileStorage.PostgresTests;

/// <summary>
/// The range strategy configured on the storage options has to shape the table that is created:
/// <see cref="PostgresFileStorageOptions.PartByRange"/> is mapped to a <c>PgPartBy</c> inside the
/// partitional module, and nothing else asserts that mapping end to end.
/// </summary>
public sealed class PostgresFileStoragePartitioningTests(
    PostgresFileStoragePartitioningTests.Fixture fixture)
    : IClassFixture<PostgresFileStoragePartitioningTests.Fixture>
{
    private const string TableName = "monthly_files";

    public sealed class Fixture : PostgresFileStorageFixture
    {
        public Fixture() : base(TableName, o => o.PartByRange = PartByRange.Month) { }
    }

    [Fact]
    public async Task PartByRangeMonth_CreatesMonthlyLeaves_NotDailyOnes()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("monthly"));

        await fixture.Sub.UploadAsync(
            new UploadFileInput { FileName = "m.txt", TenantId = 11 },
            content, fixture.CancellationToken);

        // The whole partition tree under the root: the list levels and the date leaves. With
        // PartByRange.Month a leaf is named `...__yYYYYmm`; a daily leaf would end in `dDD`.
        List<string> partitions = await fixture.DataSource.ExecuteReaderList(
            """
            WITH RECURSIVE tree AS (
                SELECT c.oid, c.relname
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = @table
                UNION ALL
                SELECT child.oid, child.relname
                FROM tree t
                JOIN pg_inherits i ON i.inhparent = t.oid
                JOIN pg_class child ON child.oid = i.inhrelid
            )
            SELECT relname FROM tree WHERE relname <> @table
            """,
            reader => reader.GetString(0),
            [new NpgsqlParameter<string>("table", TableName)],
            fixture.CancellationToken);

        Assert.NotEmpty(partitions);
        Assert.Contains(partitions, name => Regex.IsMatch(name, @"__y\d{4}m\d{2}$"));
        Assert.DoesNotContain(partitions, name => Regex.IsMatch(name, @"__y\d{4}m\d{2}d\d{2}$"));
    }
}
