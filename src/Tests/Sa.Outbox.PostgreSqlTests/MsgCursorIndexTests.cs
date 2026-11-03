using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests;

/// <summary>
/// The msg-table cursor index (<c>ix_&lt;msg table&gt;_cursor</c> on <c>(msg_seq)</c>) is what keeps
/// <c>SqlLoadConsumerGroup</c> from scanning every daily leaf of the lookback window on every poll —
/// measured on 7 × 40k rows: 56.8 ms and 10 377 shared buffers without it, 0.166 ms and 14 buffers
/// with it. <c>tenant_id</c> / <c>msg_part</c> are deliberately absent: they are the LIST partition
/// keys, constant inside a leaf, so they would only widen the key.
/// </summary>
/// <remarks>
/// These tests pin the two properties that make that true in a live database, neither of which the
/// generated-SQL test can see:
/// <list type="number">
/// <item>the index lands on the <b>root</b> on <c>msg_seq</c>, which is what turns the loader's
/// cursor predicate into an <c>Index Cond</c> rather than a filter;</item>
/// <item>every relation of the msg hierarchy carries it, <b>including a partition created after the
/// index existed</b> — that is what stops a daily rollover from silently reintroducing the scan.</item>
/// </list>
/// No scheduler is started and nothing is published: the test drives the migration explicitly.
/// </remarks>
public sealed class MsgCursorIndexTests(MsgCursorIndexTests.Fixture fixture)
    : IClassFixture<MsgCursorIndexTests.Fixture>
{
    internal const string Schema = "idx_cursor";
    internal const int TenantId = 1;

    /// <summary>Today (UTC midnight) — the first range leaf the fixture migrates for.</summary>
    internal static readonly DateTimeOffset Day = new(DateTime.UtcNow.Date, TimeSpan.Zero);

    public sealed class Fixture : PgDataSourceFixture
    {
        public Fixture()
        {
            // One delivery + one tenant, so the migration resolves a non-empty parts list and
            // therefore builds the tenant/part/day levels — the index has to reach the leaves,
            // not just the root. Neither runs: no scheduler is started and nothing is published.
            Services
                .AddSaOutbox(builder => builder
                    .WithTenants((_, s) => s.WithTenantIds(TenantId))
                    .WithDeliveries(b =>
                        b.AddDeliveryScoped<NoopConsumer, TestMessage>("g1",
                            (_, o) => o.StartImmediately().WithMaxBatchSize(1).WithNoBatchingWindow())))
                .AddSaOutboxUsingPostgreSql(cfg =>
                {
                    cfg.WithDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = ConnectionString)));
                    cfg.WithMessageSerializer(_ => OutboxMessageSerializer.Instance);
                    cfg.WithOutboxSettings((_, s) =>
                    {
                        s.TableSettings.WithSchema(Schema);
                        s.MigrationSettings.AsBackgroundJob = false;
                        s.CleanupSettings.AsBackgroundJob = false;
                    });
                });
        }
    }

    IPgDataSource DataSource => fixture.DataSource;

    CancellationToken Token => TestContext.Current.CancellationToken;

    PgOutboxTableSettings TableSettings => fixture.ServiceProvider.GetRequiredService<PgOutboxTableSettings>();

    /// <summary>The index name the builder derives: <c>ix_{msg table}_cursor</c>.</summary>
    string IndexName => $"ix_{TableSettings.Message.TableName}_cursor";

    async Task MigrateAsync(DateTimeOffset day)
    {
        IMigrationService migration = fixture.ServiceProvider
            .GetRequiredKeyedService<IMigrationService>(OutboxScope.DefaultKey);

        int executed = await migration.Migrate([day], Token);
        Assert.True(executed >= 0, $"migration reported an incomplete run (executed={executed})");
    }

    /// <summary>
    /// The msg table's partition tree (<c>pg_partition_tree</c>: root plus every descendant) and how
    /// many of those relations carry an index on <c>(msg_seq)</c>. Equal counts
    /// mean the index reached every partition. The tree is taken from the catalog rather than from a
    /// name prefix, which would also match the library's internal <c>__part$</c> cache table.
    /// </summary>
    /// <remarks>
    /// Matched by the index <b>definition</b>, not by name: PostgreSQL gives a child index its own
    /// auto-generated name (<c>{partition}_tenant_id_msg_part_msg_seq_idx</c>), so only the root
    /// keeps the name the builder derives.
    /// </remarks>
    async Task<(int Relations, int Indexed)> CountMsgRelationsAndIndexesAsync()
    {
        string sql = $"""
            SELECT
              count(*)::int,
              count(*) FILTER (WHERE EXISTS (
                SELECT 1
                  FROM pg_index i
                 WHERE i.indrelid = c.oid
                   AND pg_get_indexdef(i.indexrelid) LIKE '%(msg_seq)'))::int
              FROM pg_partition_tree('{TableSettings.GetQualifiedMsgTableName()}'::regclass) t
              JOIN pg_class c ON c.oid = t.relid;
            """;

        List<(int, int)> rows = await DataSource.ExecuteReaderList(
            sql, r => (r.GetInt32(0), r.GetInt32(1)), Token);

        return rows[0];
    }

    /// <summary>Diagnostics: every msg relation with the index definitions attached to it.</summary>
    async Task<string> DescribeMsgRelationsAsync()
    {
        string sql = $"""
            SELECT c.relname || ' [' || c.relkind::text || '] -> ' ||
                   COALESCE((SELECT string_agg(pg_get_indexdef(i.indexrelid), ' | ')
                               FROM pg_index i WHERE i.indrelid = c.oid), '<none>')
              FROM pg_partition_tree('{TableSettings.GetQualifiedMsgTableName()}'::regclass) t
              JOIN pg_class c ON c.oid = t.relid
             ORDER BY c.relname;
            """;

        List<string> rows = await DataSource.ExecuteReaderList(sql, r => r.GetString(0), Token);
        return string.Join("\n", rows);
    }

    [Fact]
    public async Task Migration_CreatesCursorIndex_OnTheRootAndOnEveryPartition()
    {
        await MigrateAsync(Day);

        // 1) Root: the exact column list, in the order SqlLoadConsumerGroup predicates on it.
        string rootDefinition = await DataSource.ExecuteReaderFirst<string>(
            $"""
            SELECT pg_get_indexdef(i.indexrelid)
              FROM pg_index i
              JOIN pg_class ix ON ix.oid = i.indexrelid
             WHERE ix.relname = '{IndexName}'
               AND i.indrelid = '{TableSettings.GetQualifiedMsgTableName()}'::regclass;
            """,
            Token);

        Assert.Contains("(msg_seq)", rootDefinition, StringComparison.Ordinal);

        // 2) Every relation of the hierarchy, leaves included, carries it. The migration for `Day`
        //    builds root → tenant → part → day leaf, so there is more than the root to check.
        var (relations, indexed) = await CountMsgRelationsAndIndexesAsync();
        Assert.True(relations >= 4, $"expected root + tenant + part + day leaf, got {relations}");
        Assert.True(relations == indexed,
            $"relations={relations} indexed={indexed}\n{await DescribeMsgRelationsAsync()}");
    }

    [Fact]
    public async Task PartitionCreatedAfterTheIndex_InheritsIt()
    {
        await MigrateAsync(Day);
        var (before, indexedBefore) = await CountMsgRelationsAndIndexesAsync();
        Assert.True(before == indexedBefore,
            $"relations={before} indexed={indexedBefore}\n{await DescribeMsgRelationsAsync()}");

        // A new day's leaf is created by a migration that runs long after the index exists. The
        // index lives on the root, so PostgreSQL attaches it to the new partition automatically —
        // this is the property that keeps a daily rollover from reintroducing the Seq Scan.
        await MigrateAsync(Day.AddDays(1));

        var (after, indexedAfter) = await CountMsgRelationsAndIndexesAsync();
        Assert.True(after > before, $"expected a new leaf partition, {before} -> {after}");
        Assert.True(after == indexedAfter,
            $"relations={after} indexed={indexedAfter}\n{await DescribeMsgRelationsAsync()}");
    }

    private sealed class NoopConsumer : IConsumer<TestMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
