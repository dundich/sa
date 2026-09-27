using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.Partitional.PostgreSql;
using System.Text;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests (no database) for the escaping in <c>SqlTemplate</c>: partition key values are
/// untrusted input that reaches the generated DDL through the partition <i>table name</i>, so
/// every identifier is quoted <b>and</b> escaped, and every literal is single-quote escaped.
/// </summary>
public class SqlTemplateEscapingTests(SqlTemplateEscapingTests.Fixture fixture) : IClassFixture<SqlTemplateEscapingTests.Fixture>
{
    /// <summary>Range suffix that <c>PgPartBy.Day</c> produces for <see cref="FixedDate"/>.</summary>
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
                    // Hostile names: a double quote and a single quote in the table name exercise
                    // identifier escaping and literal escaping on the same table.
                    schema.AddTable("we\"ird",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL"
                    )
                    .PartByList("part")
                    .TimestampAs("date")
                    ;

                    schema.AddTable("we'ird",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL"
                    )
                    .PartByList("part")
                    .TimestampAs("date")
                    ;

                    // Plain table used for the partition-value tests.
                    schema.AddTable("esc",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "tenant_id INT NOT NULL"
                    )
                    .PartByList("part", "tenant_id")
                    .TimestampAs("date")
                    ;
                });

                // A double quote in the schema name exercises schema identifier escaping.
                builder.AddSchema("sc\"h", schema =>
                {
                    schema.AddTable("t",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL"
                    )
                    .PartByList("part")
                    .TimestampAs("date")
                    ;
                });
            });
        }

        internal ISqlBuilder SqlBuilder => ServiceProvider.GetRequiredService<ISqlBuilder>();
    }

    #region helpers

    private ISqlTableBuilder Builder(string fullName)
    {
        ISqlTableBuilder? builder = fixture.SqlBuilder[fullName];
        Assert.NotNull(builder);
        return builder;
    }

    /// <summary>Length of the range partition name of the <c>esc</c> table around the two list values.</summary>
    private const int EscRangeNameOverhead = 20;

    /// <summary>The range partition name the <c>esc</c> table gets for the given two list values.</summary>
    private static string EscRangeName(string value1, string value2)
        => $"esc__{value1}__{value2}__{DaySuffix}";

    #endregion

    [Fact]
    public void Identifier_DoubleQuoteInValue_IsDoubled()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        string sql = builder.CreateSql(FixedDate, "a\"b", 27);

        // The value reaches DDL as part of the partition table name; the embedded double quote
        // must be doubled, otherwise it would close the identifier and let the rest of the
        // value be parsed as SQL.
        Assert.Contains("\"public\".\"esc__a\"\"b\"", sql);
        Assert.DoesNotContain("esc__a\"b", sql);
    }

    [Fact]
    public void Identifier_InjectionAttempt_StaysInsideTheIdentifier()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        string sql = builder.CreateSql(FixedDate, "x\"; DROP TABLE users; --", 27);

        // As an identifier the quote is doubled, so the payload cannot close it.
        Assert.Contains("\"public\".\"esc__x\"\"; DROP TABLE users; --\"", sql);

        // As a list-partition value it is confined to a quoted literal, where a double quote
        // needs no escaping. Both occurrences are inside quotes, so neither can start a statement.
        Assert.Contains("FOR VALUES IN ('x\"; DROP TABLE users; --')", sql);
    }

    [Fact]
    public void Literal_SingleQuoteInValue_IsDoubled()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        string sql = builder.CreateSql(FixedDate, "a'b", 27);

        // Inside a double-quoted identifier a raw single quote is harmless ...
        Assert.Contains("\"public\".\"esc__a'b\"", sql);

        // ... but the same qualified name is also written into the cache table as a string
        // literal, where the quote must be doubled or it would terminate the literal.
        Assert.Contains($"'\"public\".\"esc__a''b__27__{DaySuffix}\"'", sql);
    }

    [Fact]
    public void Identifier_DoubleQuoteInTableName_IsDoubled()
    {
        ISqlTableBuilder builder = Builder("public.we\"ird");

        string sql = builder.CreateSql(FixedDate, "v");

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"we\"\"ird\"", sql);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"public\";", sql);

        // The generated constraint name embeds the table name, so it is escaped too.
        Assert.Contains("CONSTRAINT \"pk_we\"\"ird\" PRIMARY KEY (\"id\",\"part\",\"date\")", sql);

        // Partitioning column lists are identifiers as well.
        Assert.Contains("PARTITION BY LIST (\"part\")", sql);
        Assert.Contains("PARTITION BY RANGE (\"date\")", sql);
    }

    [Fact]
    public void Identifier_DoubleQuoteInSchemaName_IsDoubled()
    {
        ISqlTableBuilder builder = Builder("sc\"h.t");

        string sql = builder.CreateSql(FixedDate, "v");

        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"sc\"\"h\";", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"sc\"\"h\".\"t\"", sql);
        Assert.DoesNotContain("\"sc\"h", sql);
    }

    [Fact]
    public void Literal_SingleQuoteInRootTableName_IsDoubled()
    {
        ISqlTableBuilder builder = Builder("public.we'ird");

        // SELECT id,root,part_values,part_by,from_date ... WHERE root = '<FullName>'
        string selectFrom = builder.SelectPartsFromDate;
        string selectTo = builder.SelectPartsToDate;

        Assert.Contains("WHERE root = 'public.we''ird' AND from_date >= @from_date", selectFrom);
        Assert.Contains("WHERE root = 'public.we''ird' AND to_date <= @to_date", selectTo);
    }

    [Fact]
    public void Number_LargeChoiceNum_IsRenderedWithInvariantCulture()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        string sql = builder.CreateSql(FixedDate, "some", long.MaxValue);

        // No digit grouping, no localized digits, and the full 64-bit range survives.
        Assert.Contains("FOR VALUES IN (9223372036854775807)", sql);
        Assert.Contains("\"public\".\"esc__some__9223372036854775807\"", sql);
    }

    [Fact]
    public void Number_NegativeChoiceNum_IsRenderedWithInvariantCulture()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        string sql = builder.CreateSql(FixedDate, "some", long.MinValue);

        Assert.Contains("FOR VALUES IN (-9223372036854775808)", sql);
    }

    [Fact]
    public void Identifier_Exactly63Bytes_IsAccepted()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        string value1 = new('a', 20);
        string value2 = new('b', 23);

        Assert.Equal(63, Encoding.UTF8.GetByteCount(EscRangeName(value1, value2)));

        string sql = builder.CreateSql(FixedDate, value1, value2);

        Assert.Contains($"\"public\".\"{EscRangeName(value1, value2)}\"", sql);
    }

    [Fact]
    public void Identifier_64Bytes_IsRejected()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        string value1 = new('a', 20);
        string value2 = new('b', 24);

        Assert.Equal(64, Encoding.UTF8.GetByteCount(EscRangeName(value1, value2)));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => builder.CreateSql(FixedDate, value1, value2));

        Assert.Contains("63-byte", ex.Message);
    }

    [Fact]
    public void Identifier_63Bytes_ButMoreThan63Characters_IsAccepted()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        // 'é' is one char but two UTF-8 bytes: 8 + 27 = 35 chars yet 16 + 27 = 43 bytes,
        // so the name is 55 characters but exactly 63 bytes. A byte-exact name is accepted.
        string value1 = new('é', 8);
        string value2 = new('b', 27);

        Assert.Equal(63, Encoding.UTF8.GetByteCount(EscRangeName(value1, value2)));
        Assert.True(EscRangeName(value1, value2).Length < 63);

        string sql = builder.CreateSql(FixedDate, value1, value2);

        Assert.Contains($"\"public\".\"{EscRangeName(value1, value2)}\"", sql);
    }

    [Fact]
    public void Identifier_FewerThan63Characters_ButMoreThan63Bytes_IsRejected()
    {
        ISqlTableBuilder builder = Builder("public.esc");

        // '中' is one char but three UTF-8 bytes: 25 + 1 = 26 chars, so the whole identifier is
        // only 46 characters — well under the limit. It is 20 + 75 + 1 = 96 bytes. PostgreSQL
        // truncates on bytes, so a character-count check would wrongly let this through.
        string value1 = new('中', 25);
        string value2 = "x";

        Assert.True(EscRangeName(value1, value2).Length < 63);
        Assert.True(Encoding.UTF8.GetByteCount(EscRangeName(value1, value2)) > 63);

        Assert.Throws<InvalidOperationException>(() => builder.CreateSql(FixedDate, value1, value2));
    }
}
