using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests (no database) for the SQL the archive path builds: the archive schema and the
/// <c>archived</c> cache column are created idempotently, the detach is a single
/// <c>CONCURRENTLY</c> statement, and the move/booking statements are shaped so a resumed run is a
/// no-op. The escaping of the archive schema name matters as much as every other identifier.
/// </summary>
public class ArchiveSqlTemplateTests(ArchiveSqlTemplateTests.Fixture fixture) : IClassFixture<ArchiveSqlTemplateTests.Fixture>
{
    private const string DaySuffix = "y2026m09d26";

    private static readonly DateTimeOffset FixedDate = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    public class Fixture : SaFixture
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("events",
                        "id INT NOT NULL",
                        "city TEXT NOT NULL"
                    )
                    .PartByList("city")
                    .TimestampAs("date")
                    ;
                });
            });
        }

        internal ISqlBuilder SqlBuilder => ServiceProvider.GetRequiredService<ISqlBuilder>();
    }

    private ITableSettings Settings
    {
        get
        {
            ISqlTableBuilder builder = fixture.SqlBuilder["public.events"];
            return builder.Settings;
        }
    }

    private static StrOrNum[] Moscow => ["Moscow"];

    [Fact]
    public void CacheCreate_CarriesArchivedColumn()
    {
        ISqlTableBuilder builder = fixture.SqlBuilder["public.events"];

        string sql = builder.CreateSql(FixedDate, "Moscow");

        Assert.Contains("archived boolean NOT NULL DEFAULT FALSE", sql);
    }

    [Fact]
    public void EnsureArchiveSql_CreatesSchemaAndAddsColumn_Idempotently()
    {
        string sql = Settings.EnsureArchiveSql("archive");

        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"archive\";", sql);
        Assert.Contains("ADD COLUMN IF NOT EXISTS archived boolean NOT NULL DEFAULT FALSE", sql);
    }

    [Fact]
    public void EnsureArchiveSql_EscapesSchemaName()
    {
        string sql = Settings.EnsureArchiveSql("we\"ird");

        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"we\"\"ird\";", sql);
        Assert.DoesNotContain("\"we\"ird\"", sql);
    }

    [Fact]
    public void DetachPartSql_IsSingleConcurrentStatement_OnTheParent()
    {
        string sql = Settings.DetachPartSql(Moscow, "\"public\".\"events__Moscow__y2026m09d26\"");

        // Single statement: CONCURRENTLY is illegal inside a transaction block, so it cannot be
        // batched with the SET SCHEMA that follows. PostgreSQL spells it `DETACH PARTITION name
        // CONCURRENTLY` — the qualifier trails the partition name.
        Assert.Equal(
            "ALTER TABLE \"public\".\"events__Moscow\" DETACH PARTITION \"public\".\"events__Moscow__y2026m09d26\" CONCURRENTLY;\n",
            sql);
    }

    [Fact]
    public void DetachPartSql_OnRangeOnlyTable_UsesTheRootAsParent()
    {
        // The parent is the immediate one: for a range-only table there is no list level, so the
        // parent is the root itself and the values are empty.
        ITableSettings settings = Settings;
        string sql = settings.DetachPartSql([], "\"public\".\"events__y2026m09d26\"");

        Assert.Equal(
            "ALTER TABLE \"public\".\"events\" DETACH PARTITION \"public\".\"events__y2026m09d26\" CONCURRENTLY;\n",
            sql);
    }

    [Fact]
    public void FinalizeDetachPartSql_UsesFinalize()
    {
        string sql = Settings.FinalizeDetachPartSql(Moscow, "\"public\".\"events__Moscow__y2026m09d26\"");

        Assert.Equal(
            "ALTER TABLE \"public\".\"events__Moscow\" DETACH PARTITION \"public\".\"events__Moscow__y2026m09d26\" FINALIZE;\n",
            sql);
    }

    [Fact]
    public void ArchivePartSql_MovesThenMarksArchived()
    {
        string sql = Settings.ArchivePartSql("\"public\".\"events__Moscow__y2026m09d26\"", "archive");

        Assert.Contains("ALTER TABLE \"public\".\"events__Moscow__y2026m09d26\" SET SCHEMA \"archive\";", sql);
        Assert.Contains(
            "UPDATE \"public\".\"events__part$\" SET archived = TRUE WHERE id='\"public\".\"events__Moscow__y2026m09d26\"';",
            sql);
    }

    [Fact]
    public void MarkArchivedSql_OnlyTouchesTheCacheRow()
    {
        string sql = Settings.MarkArchivedSql("\"public\".\"events__Moscow__y2026m09d26\"");

        Assert.DoesNotContain("SET SCHEMA", sql);
        Assert.Contains("UPDATE \"public\".\"events__part$\" SET archived = TRUE WHERE id=", sql);
    }

    [Fact]
    public void QualifiedName_QuotesAndEscapesBothParts()
    {
        Assert.Equal("\"we\"\"ird\".\"a\"\"b\"", SqlTemplate.QualifiedName("we\"ird", "a\"b"));
    }

    [Fact]
    public void PartStateSql_AsksAboutSourceArchiveAndAttachment()
    {
        string sql = SqlTemplate.PartStateSql(
            "\"public\".\"events__Moscow__y2026m09d26\"",
            "\"archive\".\"events__Moscow__y2026m09d26\"",
            "\"public\".\"events__Moscow\"");

        Assert.Contains("to_regclass('\"public\".\"events__Moscow__y2026m09d26\"')", sql);
        Assert.Contains("to_regclass('\"archive\".\"events__Moscow__y2026m09d26\"')", sql);
        Assert.Contains("inhparent = to_regclass('\"public\".\"events__Moscow\"')", sql);
    }
}
