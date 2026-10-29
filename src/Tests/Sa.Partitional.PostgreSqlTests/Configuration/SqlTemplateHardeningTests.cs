using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests (no database) for the text that <c>SqlTemplate</c> embeds in generated SQL from
/// sources other than the caller's live input: strings that carry a backslash, partition values that
/// would make a generated name ambiguous, partition names read back out of the cache table, and
/// developer-provided names that land inside a SQL comment.
/// </summary>
public class SqlTemplateHardeningTests(SqlTemplateHardeningTests.Fixture fixture)
    : IClassFixture<SqlTemplateHardeningTests.Fixture>
{
    private static readonly DateTimeOffset FixedDate = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    public class Fixture : SaFixture
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    // A separator that is not the default one, so the collision case is testable
                    // without every other test turning into a duplicate "__" value.
                    schema.AddTable("hard",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL")
                    .PartByList("part")
                    .TimestampAs("date")
                    .WithPartSeparator("::");

                    // A field name that contains a line break: the range annotation in the generated
                    // DDL is a single-line `--` comment, so this is the text that could end it.
                    schema.AddTable("cmt", "id INT NOT NULL")
                    .TimestampAs("da\nte");
                });
            });
        }

        internal ISqlBuilder SqlBuilder => ServiceProvider.GetRequiredService<ISqlBuilder>();
    }

    private ISqlTableBuilder Builder(string fullName)
    {
        ISqlTableBuilder? builder = fixture.SqlBuilder[fullName];
        Assert.NotNull(builder);
        return builder;
    }

    [Fact]
    public void Literal_BackslashInValue_UsesTheEscapeStringForm()
    {
        string sql = Builder("public.hard").CreateSql(FixedDate, @"a\'b");

        // A plain '…' literal only works while standard_conforming_strings is on. `E` forces
        // backslash escaping whatever the server setting is, and every backslash is doubled, so the
        // quote after it cannot swallow the closing quote of the literal.
        Assert.Contains(@"E'a\\''b'", sql);
    }

    [Fact]
    public void Literal_WithoutABackslash_KeepsThePlainForm()
    {
        string sql = Builder("public.hard").CreateSql(FixedDate, "a'b");

        Assert.Contains("FOR VALUES IN ('a''b')", sql);
        Assert.DoesNotContain("E'", sql);
    }

    [Fact]
    public void PartitionValue_ContainingTheSeparator_IsRejected()
    {
        // ("a::b") and ("a", then a value starting with ":b") would compose the same table name
        // while asking for different partition bounds, so the second CREATE TABLE would silently
        // no-op and its rows would land in the first partition.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Builder("public.hard").CreateSql(FixedDate, "a::b"));

        Assert.Contains("separator", ex.Message);
    }

    [Fact]
    public void DropPartSql_RequotesTheStoredName()
    {
        ITableSettings settings = Builder("public.hard").Settings;

        string sql = settings.DropPartSql("public.hard__y2026m09d26");

        // The DDL gets a properly quoted pair of identifiers ...
        Assert.Contains("DROP TABLE IF EXISTS \"public\".\"hard__y2026m09d26\";", sql);

        // ... while the cache row is still matched by the exact stored string.
        Assert.Contains("WHERE id='public.hard__y2026m09d26'", sql);
    }

    [Fact]
    public void DropPartSql_CraftedStoredName_StaysInsideTheIdentifier()
    {
        ITableSettings settings = Builder("public.hard").Settings;

        string sql = settings.DropPartSql("\"public\".\"x\"\"; DROP TABLE users; --\"");

        // The payload survives only as identifier text: the quote it carries is doubled, so the
        // statement still ends at the library's own semicolon.
        Assert.Contains(
            "DROP TABLE IF EXISTS \"public\".\"x\"\"; DROP TABLE users; --\";",
            sql);
    }

    [Fact]
    public void DropPartSql_MalformedStoredName_IsRejected()
    {
        ITableSettings settings = Builder("public.hard").Settings;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => settings.DropPartSql("public.x\"; DROP TABLE users; --"));

        Assert.Contains("refusing", ex.Message);
    }

    [Fact]
    public void DetachPartSql_RequotesTheStoredName()
    {
        ITableSettings settings = Builder("public.hard").Settings;

        string sql = settings.DetachPartSql(["v"], "public.hard::v::y2026m09d26");

        Assert.Contains(
            "DETACH PARTITION \"public\".\"hard::v::y2026m09d26\" CONCURRENTLY;",
            sql);
    }

    [Fact]
    public void RangeAnnotation_LineBreakInFieldName_StaysInsideTheComment()
    {
        string sql = Builder("public.cmt").CreateSql(FixedDate);

        // The annotation is a `--` comment: a raw line break in the field name would end it and turn
        // the remainder into live SQL.
        Assert.Contains("-- (da te)  part by: Day", sql);
        Assert.DoesNotContain("-- (da\nte)", sql);
    }
}
