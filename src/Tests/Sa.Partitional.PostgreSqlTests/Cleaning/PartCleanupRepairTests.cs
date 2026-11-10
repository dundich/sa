using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Cleaning;

/// <summary>
/// Cleanup after the archive has already run: a window that is written to again (the leaf is
/// re-created in the live schema), and a partition that was dropped outside the library while its
/// metadata row stayed behind.
/// </summary>
public class PartCleanupRepairTests(PartCleanupRepairTests.Fixture fixture)
    : IClassFixture<PartCleanupRepairTests.Fixture>
{
    private const string ArchiveSchema = "archive_repair";

    public class Fixture : PgDataSourceFixture<IPartRepository>
    {
        public Fixture()
        {
            Services.AddSaPartitional((_, builder) =>
            {
                builder.AddSchema(schema =>
                {
                    schema.AddTable("repair_live", "id INT NOT NULL")
                        .PartByRange(PgPartBy.Day);

                    schema.AddTable("repair_ghost", "id INT NOT NULL")
                        .PartByRange(PgPartBy.Day);

                    schema.AddTable("repair_rename", "id INT NOT NULL")
                        .PartByRange(PgPartBy.Day);

                    schema.AddTable("repair_prune", "id INT NOT NULL", "tenant TEXT NOT NULL")
                        .PartByList("tenant")
                        .PartByRange(PgPartBy.Day)
                        .AddMigration(["kept"]);
                });
            })
            .AddDataSource(configure => configure.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)))
            .AddPartCleanupSchedule(b => b.Configure((_, s) =>
            {
                s.CleanupMode = PartCleanupMode.Archive;
                s.ArchiveSchemaName = ArchiveSchema;
            }));
        }
    }

    /// <summary>A day in the past, so the cleanup window always covers it.</summary>
    private static readonly DateTimeOffset Day = new(DateTime.UtcNow.Date.AddDays(-2), TimeSpan.Zero);

    private static string Suffix => PgPartBy.Day.Fmt(Day);

    private IPartRepository Rep => fixture.Sub;

    private IPartitionManager Manager => fixture.ServiceProvider.GetRequiredService<IPartitionManager>();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<long> Count(string qualified)
        => fixture.DataSource.ExecuteScalarTypedAsync<long>($"SELECT count(*) FROM {qualified};", cancellationToken: Ct);

    private Task<long> ScalarLong(string sql)
        => fixture.DataSource.ExecuteScalarTypedAsync<long>(sql, cancellationToken: Ct);

    private Task<bool> Exists(string qualified)
        => fixture.DataSource.ExecuteScalarTypedAsync<bool>(
            "SELECT to_regclass(@name) IS NOT NULL;",
            [new NpgsqlParameter<string>("name", qualified)], Ct);

    private Task<bool> Attached(string qualified)
        => fixture.DataSource.ExecuteScalarTypedAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_inherits WHERE inhrelid = to_regclass(@t));",
            [new NpgsqlParameter<string>("t", qualified)], Ct);

    [Fact]
    public async Task ReCreatedPartition_IsVisibleAgain_AfterItWasArchived()
    {
        await Rep.Migrate([Day], Ct);

        Assert.Equal(1, await Rep.ArchivePartsToDate("repair_live", DateTimeOffset.UtcNow.AddDays(30), ArchiveSchema, Ct));
        Assert.True(await Exists($"\"{ArchiveSchema}\".\"repair_live__{Suffix}\""));

        // The archived window is written to again: the leaf is re-created in the live schema. The
        // cache row must follow (`archived` back to FALSE), otherwise it stays invisible to
        // EnsureParts and to every later cleanup pass.
        await Rep.Migrate([Day], Ct);

        Assert.True(await Exists($"public.\"repair_live__{Suffix}\""));
        Assert.Single(await Rep.GetPartsFromDate("repair_live", Day.AddDays(-1), Ct));

        // The archived copy still holds the same name, so archiving the re-created leaf would fail
        // with duplicate_table *after* the detach. The pass leaves it attached instead.
        Assert.Equal(0, await Rep.ArchivePartsToDate("repair_live", DateTimeOffset.UtcNow.AddDays(30), ArchiveSchema, Ct));
        Assert.True(await Exists($"public.\"repair_live__{Suffix}\""));
        Assert.True(await Attached($"public.\"repair_live__{Suffix}\""));
    }

    [Fact]
    public async Task GhostCacheRow_IsRemoved_WhenThePartitionIsGoneEverywhere()
    {
        await Rep.Migrate([Day], Ct);

        // Someone drops the partition by hand; the cache row stays behind.
        await fixture.DataSource.ExecuteNonQueryAsync($"DROP TABLE public.\"repair_ghost__{Suffix}\";", Ct);

        await Rep.ArchivePartsToDate("repair_ghost", DateTimeOffset.UtcNow.AddDays(30), ArchiveSchema, Ct);

        // The stale row is gone, so the next pass does not warn about the same ghost partition again.
        Assert.Equal(0, await Count("public.\"repair_ghost__part$\""));
    }

    [Fact]
    public async Task Collision_RenameExisting_ArchivesTheNewCopyUnderTheCanonicalName()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(30);

        await Rep.Migrate([Day], Ct);
        await fixture.DataSource.ExecuteNonQueryAsync(
            $"INSERT INTO public.repair_rename (id, created_at) VALUES (1, {Day.ToUnixTimeSeconds()});", Ct);

        Assert.Equal(1, await Rep.ArchivePartsToDate(
            "repair_rename", cutoff, ArchiveSchema, ArchiveCollisionMode.RenameExisting, Ct));

        // The window is written to again and expires a second time.
        await Rep.Migrate([Day], Ct);
        await fixture.DataSource.ExecuteNonQueryAsync(
            $"INSERT INTO public.repair_rename (id, created_at) VALUES (2, {Day.ToUnixTimeSeconds()});", Ct);

        Assert.True(await Exists($"public.\"repair_rename__{Suffix}\""));
        Assert.Single(await Rep.GetPartsFromDate("repair_rename", Day.AddDays(-1), Ct));

        Assert.Equal(1, await Rep.ArchivePartsToDate(
            "repair_rename", cutoff, ArchiveSchema, ArchiveCollisionMode.RenameExisting, Ct));

        string canonical = $"\"{ArchiveSchema}\".\"repair_rename__{Suffix}\"";
        string previous = $"\"{ArchiveSchema}\".\"repair_rename__{Suffix}__prev$1\"";

        // The canonical name now holds the new data; the older copy was preserved under a derived name.
        Assert.Equal(2, await ScalarLong($"SELECT id FROM {canonical};"));
        Assert.Equal(1, await ScalarLong($"SELECT id FROM {previous};"));
    }

    [Fact]
    public async Task PruneEmptyListParts_DropsEmptyUndeclaredBranches_Only()
    {
        await Rep.Migrate([Day], Ct);                                  // creates the declared "kept" branch
        await Manager.EnsureParts("repair_prune", Day, ["transient"], Ct);
        await Manager.EnsureParts("repair_prune", Day, ["busy"], Ct);

        await fixture.DataSource.ExecuteNonQueryAsync(
            $"INSERT INTO public.repair_prune (id, tenant, created_at) VALUES (3, 'busy', {Day.ToUnixTimeSeconds()});", Ct);

        Assert.True(await Exists("public.\"repair_prune__kept\""));
        Assert.True(await Exists("public.\"repair_prune__transient\""));
        Assert.True(await Exists("public.\"repair_prune__busy\""));

        int pruned = await Rep.PruneEmptyListParts("repair_prune", Ct);

        // "transient" is empty and not declared; "kept" is declared (migration would recreate it);
        // "busy" holds a row.
        Assert.Equal(1, pruned);
        Assert.True(await Exists("public.\"repair_prune__kept\""));
        Assert.False(await Exists("public.\"repair_prune__transient\""));
        Assert.True(await Exists("public.\"repair_prune__busy\""));
    }
}
