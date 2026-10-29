using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Configuration.Builder;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// The overflow-name callback is an escape hatch: it is invoked only when the regular partition name
/// is over PostgreSQL's 63-byte limit, and never for a name that fits.
/// </summary>
public class LongPartitionNameTests
{
    private static readonly DateTimeOffset Date = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

    private static ITableSettings Build(
        string tenant,
        Func<PartitionNameContext, string>? overflow = null,
        bool defaultPartition = false)
    {
        SchemaBuilder builder = new("public");

        ITableBuilder table = builder
            .AddTable("t", "id bigint NOT NULL", "tenant text NOT NULL", "created_at bigint NOT NULL")
            .PartByList("tenant")
            .PartByRange(PgPartBy.Day, "created_at");

        if (defaultPartition) table.WithDefaultPartition();
        if (overflow is not null) table.WithLongPartitionName(overflow);

        return builder.Build().Single();
    }

    [Fact]
    public void ShortName_DoesNotInvokeTheCallback()
    {
        int calls = 0;

        ITableSettings settings = Build("acme", _ =>
        {
            calls++;
            return "unused";
        });

        string sql = new SqlTableBuilder(settings).CreateSql(Date, "acme");

        Assert.Equal(0, calls);
        Assert.Contains("\"public\".\"t__acme__y2026m10d29\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void LongName_InvokesTheCallbackForTheParentAndTheLeaf()
    {
        List<PartitionNameContext> contexts = [];
        string longTenant = new string('a', 80);

        ITableSettings settings = Build(longTenant, context =>
        {
            contexts.Add(context);
            string marker = context.RangeSuffix is null ? "parent" : "leaf";
            return $"{context.RootTableName}__h${marker}";
        });

        string sql = new SqlTableBuilder(settings).CreateSql(Date, longTenant);

        Assert.True(contexts.Count >= 2);
        Assert.Contains(contexts, c => c.RangeSuffix is null && c.Values.Single().ToString() == longTenant);
        Assert.Contains(contexts, c => c.RangeSuffix == PgPartBy.Day.Fmt(Date));

        Assert.Contains("\"public\".\"t__h$parent\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"public\".\"t__h$leaf\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"t__{longTenant}\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPartition_IsReportedToTheCallback()
    {
        List<PartitionNameContext> contexts = [];
        string longTenant = new string('a', 80);

        ITableSettings settings = Build(longTenant, context =>
        {
            contexts.Add(context);
            return context.IsDefaultPartition
                ? $"{context.RootTableName}__h$def"
                : $"{context.RootTableName}__h$part";
        }, defaultPartition: true);

        string sql = new SqlTableBuilder(settings).CreateSql(Date, longTenant);

        PartitionNameContext rootDefault = contexts.First(c => c.IsDefaultPartition);
        Assert.Equal("def$", rootDefault.RangeSuffix);
        Assert.Contains("\"public\".\"t__h$def\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CallbackResult_OverTheLimit_IsRejected()
    {
        string longTenant = new string('a', 80);

        ITableSettings settings = Build(longTenant, _ => new string('x', 70));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new SqlTableBuilder(settings).CreateSql(Date, longTenant));

        Assert.Contains("WithLongPartitionName callback returned", error.Message, StringComparison.Ordinal);
        Assert.Contains("70 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCallbackResult_IsRejected()
    {
        string longTenant = new string('a', 80);

        ITableSettings settings = Build(longTenant, _ => " ");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new SqlTableBuilder(settings).CreateSql(Date, longTenant));

        Assert.Contains("empty name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CallbackIsOptional()
    {
        string longTenant = new string('a', 80);

        ITableSettings settings = Build(longTenant);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new SqlTableBuilder(settings).CreateSql(Date, longTenant));

        Assert.Contains("Breakdown", error.Message, StringComparison.Ordinal);
    }
}
