using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Configuration.Builder;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSqlTests.Configuration;



public class SqlBuilderTests(SqlBuilderTests.Fixture fixture) : IClassFixture<SqlBuilderTests.Fixture>
{
    public class Fixture : SaFixture
    {
        public Fixture() : base()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("test_0",
                        "id CHAR(26) NOT NULL",
                        "tenant_id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "part_1 TEXT NOT NULL",
                        "payload_id TEXT"
                     )
                     .PartByList("tenant_id", "part", "part_1")
                     .TimestampAs("date")
                    ;

                    schema.AddTable("test_1",
                        "id INT NOT NULL",
                        "part TEXT NOT NULL",
                        "tenant_id INT NOT NULL",
                        "lock_expires_on BIGINT NOT NULL",
                        "payload_id TEXT NOT NULL"
                    )
                    .PartByList("part", "tenant_id")
                    .TimestampAs("date")
                    ;

                    schema.AddTable("test_2",
                        "id INT NOT NULL",
                        "name TEXT NOT NULL"
                    )
                    .PartByList("name")
                    ;

                });
            });

            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema("public", schema =>
                {
                    schema.AddTable("test_3",
                        "id INT NOT NULL",
                        "text TEXT NOT NULL"
                    );
                });

                builder.AddSchema(schema =>
                {
                    schema
                        .CreateTable("test_4")
                        .AddFields(
                            "id INT NOT NULL",
                            "text TEXT NOT NULL"
                        );

                    schema
                        .AddTable("test_5",
                            "pk_id INT NOT NULL",
                            "p0 TEXT NOT NULL",
                            "p1 TEXT NOT NULL",
                            "p2 TEXT NOT NULL",
                            "p3 TEXT NOT NULL",
                            "p4 TEXT NOT NULL",
                            "p5 TEXT NOT NULL",
                            "tid INT NOT NULL",
                            "payload_id TEXT"
                        )
                        .PartByList("tid", "p0", "p1", "p2", "p3", "p4", "p5")
                        .TimestampAs("dt")
                    ;

                });
            });
        }

        internal ISqlBuilder SqlBuilder => ServiceProvider.GetRequiredService<ISqlBuilder>();
    }

    /// <summary>Fixed instant used by the DDL-shape tests.</summary>
    private static readonly DateTimeOffset Date = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);



    [Fact]
    public void PartitionalPostgreSql_SqlBuiling_Test_0()
    {
        ISqlBuilder sqlbuilder = fixture.SqlBuilder;

        ISqlTableBuilder? build = sqlbuilder["public.test_0"];
        Assert.NotNull(build);

        var expected = ("tenant_id", "part", "part_1");
        var actual =
        (
            build.Settings.PartByListFieldNames[0],
            build.Settings.PartByListFieldNames[1],
            build.Settings.PartByListFieldNames[2]
        );

        Assert.Equal(expected, actual);

        string sql = build.CreateSql(DateTimeOffset.Now, 29, "part1", "part2");
        Assert.NotEmpty(sql);
    }

    [Fact]
    public void PartitionalPostgreSql_SqlBuiling_Test_1()
    {
        ISqlBuilder sqlbuilder = fixture.SqlBuilder;

        ISqlTableBuilder? tblBuilder = sqlbuilder["public.test_1"];
        Assert.NotNull(tblBuilder);

        string sql = tblBuilder.CreateSql(DateTimeOffset.Now, "some", 27);
        Assert.NotEmpty(sql);
    }

    [Fact]
    public void PartitionalPostgreSql_SqlBuiling_Test_2()
    {
        ISqlBuilder sqlbuilder = fixture.SqlBuilder;
        ISqlTableBuilder? tblBuilder = sqlbuilder["test_2"];

        Assert.NotNull(tblBuilder);

        string sql = tblBuilder.CreateSql(DateTimeOffset.Now, "some_2");
        Assert.NotEmpty(sql);
    }

    [Fact]
    public void PartitionalPostgreSql_SqlBuiling_Test_3()
    {
        ISqlBuilder sqlbuilder = fixture.SqlBuilder;
        ISqlTableBuilder? tblBuilder = sqlbuilder["public.test_3"];

        Assert.NotNull(tblBuilder);

        ISqlTableBuilder? tblBuilder1 = sqlbuilder["public.\"test_3\""];

        Assert.NotNull(tblBuilder1);

        Assert.Equal(tblBuilder1, tblBuilder);

        var now = DateTimeOffset.Now;

        string sqlTest = tblBuilder.CreateSql(now);
        Assert.NotEmpty(sqlTest);

        string sqlTest1 = tblBuilder1.CreateSql(now);
        Assert.Equal(sqlTest, sqlTest1);
    }

    [Fact]
    public void PartitionalPostgreSql_SqlBuiling_Test_4()
    {
        ISqlBuilder sqlbuilder = fixture.SqlBuilder;

        ISqlTableBuilder? builder = sqlbuilder["test_4"];
        Assert.NotNull(builder);

        var now = DateTimeOffset.Now;
        string sql = builder.CreateSql(now);
        Assert.NotEmpty(sql);

        // Not just "not empty": the id column has to reach the DDL, or the generated primary key
        // is syntactically broken (`PRIMARY KEY (,"created_at")`).
        Assert.Equal("id", builder.Settings.IdFieldName);
        Assert.Contains("PRIMARY KEY (\"id\"", sql);
    }


    [Fact]
    public void PartitionalPostgreSql_SqlBuiling_Test_5()
    {
        ISqlBuilder sqlbuilder = fixture.SqlBuilder;

        ISqlTableBuilder? builder = sqlbuilder["test_5"];

        Assert.NotNull(builder);
        Assert.Equal(7, builder.Settings.PartByListFieldNames.Length);

        var now = DateTimeOffset.Now;
        string sql = builder.CreateSql(now, 7, "s0", "s1", "s2", "s3", "s4", "s5");
        Assert.NotEmpty(sql);
    }

    [Fact]
    public void PartitionalPostgreSql_CheckIdName_Test()
    {
        ISqlBuilder sqlbuilder = fixture.SqlBuilder;

        ISqlTableBuilder? builder = sqlbuilder["test_5"];

        Assert.NotNull(builder);
        Assert.Equal("pk_id", builder.Settings.IdFieldName);
    }

    [Theory]
    // A leading tab used to be glued to the name by Split(' '), and the surrounding quotes were
    // kept, so the DDL ended up with """id"".
    [InlineData("id INT NOT NULL", "id")]
    [InlineData("\tid INT NOT NULL", "id")]
    [InlineData("  id   INT NOT NULL", "id")]
    [InlineData("\"id\" INT NOT NULL", "id")]
    [InlineData("\"my id\" INT NOT NULL", "my id")]
    public void IdFieldName_IsCutAtTheFirstWhitespace_AndUnquoted(string firstField, string expected)
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("tbl", firstField, "payload TEXT");

        Assert.Equal(expected, builder.Build().Single().IdFieldName);
    }

    [Fact]
    public void Build_Fails_WhenTheTableHasNoFields()
    {
        // Fail fast beats emitting `PRIMARY KEY (,"created_at")` and letting the database refuse it.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new SchemaBuilder("public").CreateTable("no_fields").Build());

        Assert.Contains("no_fields", error.Message);
    }

    [Fact]
    public void WithPartTablePostfix_RejectsNullAndWhitespace()
    {
        SchemaBuilder builder = new("public");

        // The guard used to check nameof(postfix) - a constant "postfix" - so it never fired.
        Assert.ThrowsAny<ArgumentException>(() => builder.CreateTable("t").WithPartTablePostfix(null!));
        Assert.ThrowsAny<ArgumentException>(() => builder.CreateTable("t").WithPartTablePostfix(""));
        Assert.ThrowsAny<ArgumentException>(() => builder.CreateTable("t").WithPartTablePostfix("   "));
    }

    [Fact]
    public void WithPartSeparator_RejectsNullWhitespaceAndQuote()
    {
        SchemaBuilder builder = new("public");

        // Same reasoning as the postfix: an empty separator glues the values together, so ["a","b"]
        // and ["ab"] would name one and the same partition table, and a quote would have to be
        // escaped in every generated identifier.
        Assert.ThrowsAny<ArgumentException>(() => builder.CreateTable("t").WithPartSeparator(null!));
        Assert.ThrowsAny<ArgumentException>(() => builder.CreateTable("t").WithPartSeparator(""));
        Assert.ThrowsAny<ArgumentException>(() => builder.CreateTable("t").WithPartSeparator("   "));
        Assert.ThrowsAny<ArgumentException>(() => builder.CreateTable("t").WithPartSeparator("\""));
        Assert.ThrowsAny<ArgumentException>(() => builder.CreateTable("t").WithPartSeparator("_x\""));
    }

    [Fact]
        public void WithPartSeparator_KeepsTheSeparatorItWasGiven()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("sep", "id INT NOT NULL", "part TEXT NOT NULL")
            .PartByList("part")
            .TimestampAs("date")
            .WithPartSeparator("-");

        Assert.Equal("-", builder.Build().Single().SqlPartSeparator);
    }

    [Fact]
    public void WithoutWithPartSeparator_TheDefaultIsUsed()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("def", "id INT NOT NULL");

        Assert.Equal("__", builder.Build().Single().SqlPartSeparator);
    }

    [Fact]
    public void Build_Fails_WhenTheCacheTableNameExceedsTheIdentifierLimit()
    {
        // 63 bytes, and PostgreSQL truncates silently - the DDL would then never match the name
        // the cache table stores.
        string tooLong = new('t', 70);

        SchemaBuilder builder = new("public");
        builder.AddTable(tooLong, "id INT NOT NULL");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("63", error.Message);
    }

    [Fact]
    public void PartByRange_TimestampAsWins_RegardlessOfCallOrder()
    {
        ITableSettings first = BuildWithTimestampOrder(timestampAsFirst: true);
        ITableSettings second = BuildWithTimestampOrder(timestampAsFirst: false);

        // TimestampAs is the more specific declaration, so the result must not depend on the order
        // of the two calls.
        Assert.Equal("dt", first.PartByRangeFieldName);
        Assert.Equal(first.PartByRangeFieldName, second.PartByRangeFieldName);
    }

    private static ITableSettings BuildWithTimestampOrder(bool timestampAsFirst)
    {
        SchemaBuilder builder = new("public");
        ITableBuilder table = builder
            .AddTable("t", "id INT NOT NULL", "created_at BIGINT NOT NULL", "dt BIGINT NOT NULL");

        if (timestampAsFirst)
        {
            table.TimestampAs("dt").PartByRange(PgPartBy.Month, "created_at");
        }
        else
        {
            table.PartByRange(PgPartBy.Month, "created_at").TimestampAs("dt");
        }

        return builder.Build().Single();
    }

    [Fact]
    public void PartByRange_FallsBackToItsArgument_WhenTimestampAsWasNotCalled()
    {
        SchemaBuilder builder = new("public");
        ITableBuilder table = builder.AddTable("t", "id INT NOT NULL", "created_at BIGINT NOT NULL");
        table.PartByRange(PgPartBy.Month, "created_at");

        Assert.Equal("created_at", builder.Build().Single().PartByRangeFieldName);
    }

    [Fact]
    public void WithDefaultPartition_IsOffByDefault()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "part TEXT NOT NULL").PartByList("part");

        ITableSettings settings = builder.Build().Single();

        Assert.False(settings.DefaultPartition);
        Assert.DoesNotContain("-- DEFAULT PARTITION", settings.CreateRootSql());
    }

    [Fact]
    public void WithDefaultPartition_CreatesARootDefaultPartition()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "part TEXT NOT NULL")
            .PartByList("part")
            .WithDefaultPartition();

        ITableSettings settings = builder.Build().Single();

        Assert.True(settings.DefaultPartition);

        string sql = settings.CreateRootSql();

        // A partitioned table holds at most one DEFAULT partition per parent; the root one is emitted
        // together with the root DDL.
        Assert.Contains("-- DEFAULT PARTITION", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"t__def$\"", sql);
        Assert.Contains("PARTITION OF \"public\".\"t\"\nDEFAULT", sql);
    }

    [Fact]
    public void WithDefaultPartition_UsesTheConfiguredSeparator()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "part TEXT NOT NULL")
            .PartByList("part")
            .WithPartSeparator("-")
            .WithDefaultPartition();

        string sql = builder.Build().Single().CreateRootSql();

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"t-def$\"", sql);
    }

    [Fact]
    public void WithDefaultPartition_WorksForARangeOnlyTable()
    {
        // No list columns: the root is range-partitioned, so the default catches out-of-range dates.
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "created_at BIGINT NOT NULL")
            .PartByRange(PgPartBy.Day)
            .WithDefaultPartition();

        ITableSettings settings = builder.Build().Single();

        Assert.True(settings.DefaultPartition);

        string sql = settings.CreateRootSql();

        Assert.Contains("PARTITION BY RANGE (\"created_at\")", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"t__def$\"", sql);
        Assert.Contains("PARTITION OF \"public\".\"t\"\nDEFAULT", sql);
    }

    [Fact]
    public void WithDefaultPartition_EmitsADefaultAtEveryLevel()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t",
                "id INT NOT NULL",
                "country TEXT NOT NULL",
                "city TEXT NOT NULL",
                "created_at BIGINT NOT NULL")
            .PartByList("country", "city")
            .PartByRange(PgPartBy.Day, "created_at")
            .WithDefaultPartition();

        ITableSettings settings = builder.Build().Single();
        string sql = new SqlTableBuilder(settings).CreateSql(Date, "RU", "Moscow");

        // One DEFAULT per partitioned parent: root, the country level, and the range level.
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"t__def$\"", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"t__RU__def$\"", sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"t__RU__Moscow__def$\"", sql);
    }

    [Fact]
    public void WithDefaultPartition_MoveBackIsOVERRIDINGSystemValue()
    {
        // The reconciliation's move-back re-inserts the staged id explicitly; an OVERRIDING clause
        // keeps it insertable when the id column is a GENERATED ALWAYS identity.
        SchemaBuilder builder = new("public");
        builder.AddTable("t",
                "id BIGINT GENERATED ALWAYS AS IDENTITY",
                "created_at BIGINT NOT NULL")
            .PartByRange(PgPartBy.Day, "created_at")
            .WithDefaultPartition();

        string sql = new SqlTableBuilder(builder.Build().Single()).CreateSql(Date);

        Assert.Contains("INSERT INTO \"public\".\"t__y2026m09d26\" OVERRIDING SYSTEM VALUE", sql);
    }

    [Fact]
    public void WithoutDefaultPartition_NoReconciliationIsEmitted()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t",
                "id INT NOT NULL",
                "country TEXT NOT NULL",
                "created_at BIGINT NOT NULL")
            .PartByList("country")
            .PartByRange(PgPartBy.Day, "created_at");

        ITableSettings settings = builder.Build().Single();
        string sql = new SqlTableBuilder(settings).CreateSql(Date, "RU");

        Assert.DoesNotContain("DEFAULT reconciliation", sql);
        Assert.DoesNotContain("DEFAULT PARTITION", sql);
    }

    [Fact]
    public void CreateSql_RejectsAWrongNumberOfPartValues()
    {
        // The list depth is fixed by the configuration, so a different number of values cannot
        // describe the same table. It used to be tolerated and produced a tree whose deepest level
        // had no range leaf, which only failed later, on INSERT.
        ISqlTableBuilder builder = fixture.SqlBuilder["public.test_0"]!;

        ArgumentException tooFew = Assert.Throws<ArgumentException>(
            () => builder.CreateSql(Date, 29, "part1"));

        Assert.Contains("3", tooFew.Message);
        Assert.Contains("tenant_id", tooFew.Message);
        Assert.Equal("partValues", tooFew.ParamName);

        Assert.Throws<ArgumentException>(() => builder.CreateSql(Date, 29, "part1", "part2", "extra"));
    }

    [Fact]
    public void CreateSql_RejectsAPartValueForARangeOnlyTable()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "created_at BIGINT NOT NULL")
            .PartByRange(PgPartBy.Day);

        ISqlTableBuilder tableBuilder = new SqlTableBuilder(builder.Build().Single());

        Assert.Throws<ArgumentException>(() => tableBuilder.CreateSql(Date, "unexpected"));
        Assert.NotEmpty(tableBuilder.CreateSql(Date));
    }

    [Fact]
    public void Build_RejectsAListColumnThatNoFieldDeclares()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "tenant TEXT NOT NULL")
            .PartByList("tenant_id");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("tenant_id", error.Message);
        Assert.Contains("public.t", error.Message);
    }

    [Fact]
    public void Build_RejectsTheSameListColumnTwice()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "tenant TEXT NOT NULL")
            .PartByList("tenant", "tenant");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("tenant", error.Message);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("timetz")]
    [InlineData("integer")]
    [InlineData("numeric")]
    public void Build_RejectsAnUnsupportedRangeColumnType(string type)
    {
        // These types cannot hold the generated range bounds; only bigint, timestamp and date are
        // valid range keys.
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", $"created_at {type} NOT NULL")
            .PartByRange(PgPartBy.Day);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("created_at", error.Message);
        Assert.Contains("not a supported range key", error.Message);
    }

    [Theory]
    [InlineData("bigint", RangeKeyType.UnixSeconds)]
    [InlineData("BIGINT", RangeKeyType.UnixSeconds)]
    [InlineData("timestamptz", RangeKeyType.TimestampTz)]
    [InlineData("TIMESTAMP", RangeKeyType.Timestamp)]
    [InlineData("timestamp with time zone", RangeKeyType.TimestampTz)]
    [InlineData("timestamp without time zone", RangeKeyType.Timestamp)]
    [InlineData("timestamp(3) with time zone", RangeKeyType.TimestampTz)]
    [InlineData("date", RangeKeyType.Date)]
    public void Build_DetectsTheRangeKeyType(string type, RangeKeyType expected)
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", $"created_at {type} NOT NULL")
            .PartByRange(PgPartBy.Day);

        Assert.Equal(expected, builder.Build().Single().RangeKeyType);
    }

    [Fact]
    public void Build_UsesTheExplicitRangeKeyOverride()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "created_at my_timestamp_domain NOT NULL")
            .PartByRange(PgPartBy.Day)
            .WithRangeKey(RangeKeyType.TimestampTz);

        Assert.Equal(RangeKeyType.TimestampTz, builder.Build().Single().RangeKeyType);
    }

    [Fact]
    public void Build_AcceptsABigintRangeColumn()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "created_at BIGINT NOT NULL")
            .PartByRange(PgPartBy.Day);

        Assert.Equal("created_at", builder.Build().Single().PartByRangeFieldName);
    }

    [Theory]
    [InlineData("timestamptz", "FOR VALUES FROM ('2026-10-29 00:00:00+00') TO ('2026-10-30 00:00:00+00')")]
    [InlineData("timestamp", "FOR VALUES FROM ('2026-10-29 00:00:00') TO ('2026-10-30 00:00:00')")]
    [InlineData("date", "FOR VALUES FROM ('2026-10-29') TO ('2026-10-30')")]
    public void RangeKeyType_RendersBoundsInTheColumnType(string type, string expected)
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", $"created_at {type} NOT NULL")
            .PartByRange(PgPartBy.Day);

        ITableSettings settings = builder.Build().Single();
        DateTimeOffset date = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

        string sql = new SqlTableBuilder(settings).CreateSql(date);

        Assert.Contains(expected, sql, StringComparison.Ordinal);

        // The cache keeps Unix seconds regardless of the key type.
        Assert.Contains($",{date.ToUnixTimeSeconds()},", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RangePartPredicate_UsesTheRangeKeyType()
    {
        SchemaBuilder builder = new("public");
        builder.AddTable("t", "id INT NOT NULL", "created_at timestamptz NOT NULL")
            .PartByRange(PgPartBy.Day);

        ITableSettings settings = builder.Build().Single();
        string predicate = settings.RangePartPredicate(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("\"created_at\" >= '2026-10-29 00:00:00+00'", predicate, StringComparison.Ordinal);
        Assert.Contains("\"created_at\" < '2026-10-30 00:00:00+00'", predicate, StringComparison.Ordinal);
    }
}
