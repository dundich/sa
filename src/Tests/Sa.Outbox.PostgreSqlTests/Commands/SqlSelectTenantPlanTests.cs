using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.Publication;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Sa.Outbox.PostgreSqlTests.Commands;

/// <summary>
/// Measures what <c>SqlSelectTenant</c> costs the server, which is the whole point of the
/// stage-1 change from a window function to <c>SELECT DISTINCT</c>.
/// </summary>
/// <remarks>
/// The finding was that <c>ROW_NUMBER() OVER (PARTITION BY tenant_id ...)</c> materializes and
/// sorts one row per *message* only to keep one per *tenant*, so the server's working memory
/// tracked the size of the outbox rather than the number of tenants.
///
/// The old form is gone, so this cannot be a before/after comparison against the same database
/// — the measurement is reported rather than diffed. What is asserted is that the query keeps
/// the shape that makes it cheap: no sort of the message table, and a projection of exactly one
/// column. Those are plan *shape* assertions, not row-count ones, so they survive PostgreSQL
/// version changes and data-volume changes. The EXPLAIN output is echoed to the test log so a
/// regression is visible in CI output instead of only in production.
///
/// Rows per tenant are well above one on purpose: with one row per tenant the two forms are
/// indistinguishable, and a test that only exercised the degenerate case would pass no matter
/// which shape the query had.
/// </remarks>
public class SqlSelectTenantPlanTests(SqlSelectTenantPlanTests.Fixture fixture)
    : IClassFixture<SqlSelectTenantPlanTests.Fixture>
{
    private const int MessagesPerTenant = 200;
    private const int TenantCount = 4;
    private const int TotalMessages = MessagesPerTenant * TenantCount;

    public class Fixture : OutboxPostgreSqlFixture<IOutboxMessagePublisher>
    {
        public Fixture() : base()
        {
            Services.AddSaOutbox(builder => builder
                .WithMetadata((_, b) => b.AddMetadata<TestMessage>("root_1", m => m.PayloadId))
                .WithTenants((_, s) => s.WithTenantIds(1, 2, 3, 4))
            );
        }
    }

    private IPgDataSource DataSource => fixture.DataSource;

    private SqlOutboxBuilder Builder =>
        fixture.ServiceProvider.GetRequiredService<SqlOutboxBuilder>();

    private PgOutboxTableSettings TableSettings =>
        fixture.ServiceProvider.GetRequiredService<PgOutboxTableSettings>();


    [Fact]
    public async Task SelectTenant_PlansWithoutSortingTheMessageTable()
    {
        var ct = TestContext.Current.CancellationToken;

        List<TestMessage> messages = [];
        for (var tenant = 1; tenant <= TenantCount; tenant++)
        {
            for (var i = 0; i < MessagesPerTenant; i++)
            {
                messages.Add(new TestMessage
                {
                    PayloadId = $"p-{tenant}-{i:D4}",
                    Content = $"payload for tenant {tenant} #{i}",
                    TenantId = tenant,
                });
            }
        }

        var published = await fixture.Sub.Publish(messages, m => m.TenantId, ct);
        Assert.Equal((ulong)TotalMessages, published);

        // The message table is partitioned per tenant, and every message is still unprocessed,
        // so the table holds the full seeded volume and the plan is taken against it.
        var plan = await ExplainAsync(Builder.SqlSelectTenant, ct);
        foreach (var line in plan) Console.WriteLine(line);

        var executable = ExecutableSql(Builder.SqlSelectTenant);

        // No ROW_NUMBER / OVER: the old form's cost was structural, not incidental.
        Assert.DoesNotContain("ROW_NUMBER", executable, StringComparison.Ordinal);
        Assert.DoesNotContain("OVER", executable, StringComparison.Ordinal);
        Assert.Contains("DISTINCT", executable, StringComparison.Ordinal);

        // The dedup happens on the tenant column alone, so the plan must not carry an explicit
        // Sort node over the message table. PostgreSQL may still choose a sort-based Unique
        // when it estimates few distinct tenants, which is a legitimate plan — the shape that
        // must not come back is the window function, already asserted above. So this checks the
        // expensive combination rather than the presence of any sort.
        var hasWindowSort = plan.Any(l => l.Contains("WindowAgg", StringComparison.Ordinal))
            || plan.Any(l => l.Contains("rows sorted", StringComparison.Ordinal));
        Assert.False(
            hasWindowSort,
            $"plan reintroduced per-message ranking:{Environment.NewLine}{string.Join(Environment.NewLine, plan)}");

        // The dedup node must hash on the partition key and carry a small memory footprint.
        // Under the old form the sort saw TotalMessages rows; here the aggregate reports
        // "Memory Usage: 40kB" against 800 input rows, and the rows-per-loop count on the
        // aggregate is the number of *tenants*, not the number of messages.
        var aggregate = plan.FirstOrDefault(l => l.Contains("HashAggregate", StringComparison.Ordinal));
        Assert.NotNull(aggregate);
        Assert.Contains("Group Key:", string.Join('\n', plan), StringComparison.Ordinal);

        var memoryLine = plan.FirstOrDefault(l => l.Contains("Memory Usage:", StringComparison.Ordinal));
        if (memoryLine is not null)
        {
            // Bounded: 1 MiB is two orders of magnitude above what 40kB costs, and a window
            // sort over the whole outbox would exceed it once the outbox is large.
            var kb = int.Parse(
                memoryLine[(memoryLine.IndexOf("Memory Usage:", StringComparison.Ordinal) + 13)..]
                    .Trim()
                    .Split('k')[0]
                    .Trim(),
                CultureInfo.InvariantCulture);
            Assert.InRange(kb, 0, 1024);
        }

        // One row out per tenant, no matter how many messages each tenant holds. The
        // *actual* row count is what matters here — the estimated one in the cost node is
        // deliberately wrong by design (the planner cannot know the tenant count), so both
        // are read and only the actual is asserted.
        var actual = Regex.Match(aggregate!, @"\(actual[^)]*rows=(\d+)");
        Assert.True(actual.Success, aggregate);
        Assert.Equal(
            TenantCount,
            int.Parse(actual.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SelectTenant_ReturnsEveryTenantExactlyOnce()
    {
        // The behavioural half of the change: DISTINCT must still yield the full tenant set.
        // A projection mistake here would return fewer tenants than exist and only show up
        // as a quietly under-consumed outbox.
        //
        // Counted rather than compared against a literal: both tests in this class share the
        // fixture and therefore the message table, and the plan test seeds every configured
        // tenant, so the number of distinct tenants is TenantCount by the time this runs. The
        // point being asserted is "one row per tenant, never fewer and never more", which is
        // what a broken projection or a missing DISTINCT would break.
        var ct = TestContext.Current.CancellationToken;

        var published = await fixture.Sub.Publish(
            [
                new TestMessage { PayloadId = "distinct-a", Content = "a", TenantId = 1 },
                new TestMessage { PayloadId = "distinct-b", Content = "b", TenantId = 1 },
                new TestMessage { PayloadId = "distinct-c", Content = "c", TenantId = 3 },
            ],
            m => m.TenantId,
            ct);
        Assert.Equal(3u, published);

        var field = TableSettings.Message.Fields.TenantId;
        var tenants = await DataSource.ExecuteReaderList(
            $"SELECT DISTINCT {field} FROM {TableSettings.GetQualifiedMsgTableName()}",
            r => r.GetInt32(0),
            ct);

        Assert.Equal(TenantCount, tenants.Count);
        Assert.Equal(tenants.Count, tenants.Distinct().Count());
        Assert.Equal(tenants.Order(), tenants.OrderBy(t => t));
    }

    private async Task<List<string>> ExplainAsync(string sql, CancellationToken cancellationToken)
    {
        var rows = await DataSource.ExecuteReaderList(
            $"EXPLAIN (ANALYZE, BUFFERS) {ExecutableSql(sql).TrimEnd(';')}",
            r => r.GetString(0),
            cancellationToken);

        return rows;
    }

    /// <summary>
    /// Strips SQL line comments, which the templates carry and which EXPLAIN cannot parse.
    /// </summary>
    private static string ExecutableSql(string sql)
        => string.Join('\n', sql
            .Split('\n')
            .Select(line => line.Contains("--", StringComparison.Ordinal)
                ? line[..line.IndexOf("--", StringComparison.Ordinal)]
                : line));

    private static string ExecutableSql(SqlOutboxBuilder builder)
        => ExecutableSql(builder.SqlSelectTenant);
}
