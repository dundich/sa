using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests (no database) for how the range-partition column is detected in the declared field
/// list. The root <c>CREATE TABLE</c> adds that column itself when it is missing, so a wrong answer
/// either duplicates the column (PostgreSQL: <c>column "created_at" specified more than once</c>) or
/// leaves the <c>PRIMARY KEY</c> and <c>PARTITION BY RANGE</c> pointing at a column that was never
/// created.
/// </summary>
/// <remarks>
/// The assertion counts the column <i>definition</i> rather than the bare name: a declared field is
/// emitted verbatim, and the definition the builder adds for a missing column is the same text, so a
/// second occurrence is exactly the duplicate PostgreSQL would reject.
/// </remarks>
public class SqlRangeColumnDetectionTests(SqlRangeColumnDetectionTests.Fixture fixture) : IClassFixture<SqlRangeColumnDetectionTests.Fixture>
{
    /// <summary>The range column, declared.</summary>
    private const string Declared = "\"created_at\" bigint NOT NULL";

    /// <summary>The same column with whitespace around the quoted name.</summary>
    private const string DeclaredPadded = "   \"created_at\"   bigint NOT NULL";

    /// <summary>The definition the builder adds when the column is not declared at all.</summary>
    private const string Generated = "\"ts\" bigint NOT NULL";

    public class Fixture : SaFixture
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    // Unquoted in the configured name, quoted in the field list - the happy path.
                    schema.AddTable("plain", "id INT NOT NULL", Declared)
                        .PartByRange(PgPartBy.Day, "created_at");

                    // A leading quote used to hide the column from the lookup, which then added it a
                    // second time.
                    schema.AddTable("quoted", "id INT NOT NULL", Declared)
                        .PartByRange(PgPartBy.Day, "created_at");

                    // Whitespace around the quoted name.
                    schema.AddTable("padded", "id INT NOT NULL", DeclaredPadded)
                        .PartByRange(PgPartBy.Day, "created_at");

                    // A field whose name merely starts with the range field name must not satisfy the
                    // lookup - the real column is still missing and has to be added.
                    schema.AddTable("prefixed", "id INT NOT NULL", "\"created_at_idx\" bigint NOT NULL")
                        .PartByRange(PgPartBy.Day, "created_at");

                    // A range column under a custom name, declared nowhere.
                    schema.AddTable("missing", "id INT NOT NULL", "\"payload\" TEXT")
                        .PartByRange(PgPartBy.Day, "ts");
                });
            });
        }

        internal ISqlBuilder SqlBuilder => ServiceProvider.GetRequiredService<ISqlBuilder>();
    }

    private static readonly DateTimeOffset Date = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    private string CreateSql(string fullName)
    {
        ISqlTableBuilder builder = Assert.IsAssignableFrom<ISqlTableBuilder>(fixture.SqlBuilder[fullName]);
        return builder.CreateSql(Date);
    }

    [Theory]
    [InlineData("public.plain", Declared)]
    [InlineData("public.quoted", Declared)]
    [InlineData("public.padded", DeclaredPadded)]
    [InlineData("public.prefixed", Declared)]
    [InlineData("public.missing", Generated)]
    public void RangeColumn_IsDeclaredExactlyOnce(string table, string columnDefinition)
    {
        Assert.Equal(1, CountOccurrences(CreateSql(table), columnDefinition));
    }

    [Theory]
    [InlineData("public.plain", "created_at")]
    [InlineData("public.quoted", "created_at")]
    [InlineData("public.padded", "created_at")]
    [InlineData("public.prefixed", "created_at")]
    [InlineData("public.missing", "ts")]
    public void PartitionAndPrimaryKey_UseTheRangeColumn(string table, string column)
    {
        string sql = CreateSql(table);

        Assert.Contains($"PARTITION BY RANGE (\"{column}\")", sql);
        Assert.Contains($"PRIMARY KEY (\"id\",\"{column}\")", sql);
    }

    [Fact]
    public void PrefixedField_KeepsItsOwnDefinition()
    {
        Assert.Equal(1, CountOccurrences(CreateSql("public.prefixed"), "\"created_at_idx\" bigint NOT NULL"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
