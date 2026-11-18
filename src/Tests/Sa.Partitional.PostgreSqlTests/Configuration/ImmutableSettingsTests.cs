using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Configuration.Builder;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// The settings produced by <c>Build()</c> are an immutable snapshot: a consumer cannot rewrite the
/// field declarations the DDL is generated from.
/// </summary>
public class ImmutableSettingsTests
{
    [Fact]
    public void Fields_AreReadOnly()
    {
        SchemaBuilder schema = new("public");
        schema.AddTable("t", "id bigint NOT NULL", "created_at bigint NOT NULL")
            .PartByRange(PgPartBy.Day);

        ITableSettings settings = Assert.Single(schema.Build());

        Assert.False(settings.Fields is string[]);

        IList<string>? mutable = settings.Fields as IList<string>;
        Assert.NotNull(mutable);
        Assert.True(mutable!.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutable[0] = "rewritten bigint NOT NULL");
    }

    [Fact]
    public void ListFieldNames_AreReadOnly()
    {
        SchemaBuilder schema = new("public");
        schema.AddTable("t", "id bigint NOT NULL", "tenant text NOT NULL", "created_at bigint NOT NULL")
            .PartByList("tenant")
            .PartByRange(PgPartBy.Day);

        ITableSettings settings = Assert.Single(schema.Build());

        Assert.False(settings.PartByListFieldNames is string[]);

        IList<string>? mutable = settings.PartByListFieldNames as IList<string>;
        Assert.NotNull(mutable);
        Assert.Throws<NotSupportedException>(() => mutable![0] = "other_column");
    }
}
