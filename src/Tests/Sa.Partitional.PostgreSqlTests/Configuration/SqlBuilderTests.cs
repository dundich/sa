using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Configuration;



public class SqlBuilderTests(SqlBuilderTests.Fixture fixture) : IClassFixture<SqlBuilderTests.Fixture>
{
    public class Fixture : SaFixture
    {
        public Fixture() : base()
        {
            Services.AddPartitional((_, builder) =>
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

            Services.AddPartitional((_, builder) =>
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
                        .CreateTable("test_4");

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
}
