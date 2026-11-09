using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql.Configuration.Builder;
using Sa.Partitional.PostgreSql.Settings;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// Shape of the batches a migration executes. The schema/root/cache-table DDL is emitted once per
/// table; re-emitting it for every date and every list value ran the cache table's
/// <c>ALTER TABLE … ADD COLUMN</c> (an <c>ACCESS EXCLUSIVE</c> lock) hundreds of times per tick.
/// </summary>
public class MigrateBatchSqlTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    private static async Task<List<string>> Batches(SchemaBuilder schema, StrOrNum[][] values)
    {
        SqlBuilder builder = new(new TableSettingsStorage(schema.Build()));

        List<string> batches = [];
        await foreach (MigrateBatch batch in builder.MigrateSql([Day, Day.AddDays(1)], _ => Task.FromResult(values)))
        {
            Assert.Null(batch.Error);
            batches.Add(batch.Sql!);
        }

        return batches;
    }

    [Fact]
    public async Task RangeOnlyTable_EmitsRootAndCacheDdlOnce()
    {
        SchemaBuilder schema = new("public");
        schema.AddTable("t", "id bigint NOT NULL", "created_at bigint NOT NULL")
            .PartByRange(PgPartBy.Day);

        List<string> batches = await Batches(schema, []);

        Assert.Equal(2, batches.Count);   // one per date, as before

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"t\" (", batches[0], StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS \"public\".\"t\" (", batches[1], StringComparison.Ordinal);

        // The cache table's DDL (and its ALTER … ADD COLUMN lock) is paid once, not per leaf.
        Assert.Equal(1, batches.Count(sql => sql.Contains("ALTER TABLE IF EXISTS \"public\".\"t__part$\"", StringComparison.Ordinal)));

        // The metadata row is still written for every leaf.
        Assert.All(batches, sql => Assert.Contains("INSERT INTO \"public\".\"t__part$\"", sql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListTable_EmitsRootAndCacheDdlOnce_PerTableNotPerValueAndDate()
    {
        SchemaBuilder schema = new("public");
        schema.AddTable("t_list", "id bigint NOT NULL", "tenant text NOT NULL", "created_at bigint NOT NULL")
            .PartByList("tenant")
            .PartByRange(PgPartBy.Day);

        List<string> batches = await Batches(schema, [["acme"], ["globex"]]);

        Assert.Equal(4, batches.Count);   // 2 values × 2 dates

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"t_list\" (", batches[0], StringComparison.Ordinal);
        Assert.All(batches.Skip(1), sql =>
            Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS \"public\".\"t_list\" (", sql, StringComparison.Ordinal));

        Assert.Equal(1, batches.Count(sql => sql.Contains("ALTER TABLE IF EXISTS \"public\".\"t_list__part$\"", StringComparison.Ordinal)));
        Assert.All(batches, sql => Assert.Contains("INSERT INTO \"public\".\"t_list__part$\"", sql, StringComparison.Ordinal));

        // Both branches (and both dates) are still created.
        Assert.Contains(batches, sql => sql.Contains("\"t_list__acme__", StringComparison.Ordinal));
        Assert.Contains(batches, sql => sql.Contains("\"t_list__globex__", StringComparison.Ordinal));
    }
}
