using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;
using System.Text;

namespace Sa.Outbox.PostgreSqlTests.Commands;

/// <summary>
/// Pins the shape of the generated SQL for the changes made in review stage 1.
/// </summary>
/// <remarks>
/// These are not tests of behaviour — the delivery suites cover that. They exist because every
/// one of the three properties asserted here is something a later reader would plausibly
/// "restore", each time for an apparently good reason:
///
/// <list type="bullet">
/// <item><c>SqlSelectTenant</c> would be rewritten back to a window function by anyone who wants
/// deterministic row order, reintroducing an O(messages) sort on the server.</item>
/// <item>The absence of <c>ON CONFLICT</c> in <c>SqlFinishDelivery</c> and its presence in
/// <c>SqlError</c> look like an inconsistency, and "symmetry" is a good-sounding reason to add
/// one back. The two differ on purpose: the log table's key is BIGSERIAL and cannot conflict,
/// the error table's key is a hash of the message and conflicts deliberately.</item>
/// <item>The <c>@to</c> placeholder is gone, replaced by three separately named parameters. A
/// future change that points <c>@rty_at</c> at <c>filter.NowDate</c> would look like a cleanup
/// and would quietly remove the batching-window delay before an un-backoffed retry.</item>
/// </list>
///
/// No database required.
/// </remarks>
public class SqlOutboxBuilderTests
{
    private static SqlOutboxBuilder Create()
        => new(new PgOutboxTableSettings());

    /// <summary>
    /// Strips SQL line comments. Both templates carry comments that name the very keywords
    /// these tests assert on, and the property under test is about executable SQL — otherwise
    /// documenting the absence of a clause would fail the test that checks for it.
    /// </summary>
    private static string ExecutableSql(string sql)
        => string.Join('\n', sql
            .Split('\n')
            .Select(line => line.Contains("--", StringComparison.Ordinal) ? line[..line.IndexOf("--", StringComparison.Ordinal)] : line));

    [Fact]
    public void SqlSelectTenant_UsesDistinct_NotAWindowFunction()
    {
        var sql = ExecutableSql(Create().SqlSelectTenant);

        Assert.Contains("SELECT DISTINCT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ROW_NUMBER", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OVER", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ranked", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlSelectTenant_ProjectsOnlyTheTenantColumn()
    {
        // SelectTenantCommand reads by column name, so DISTINCT must not rename or
        // hide the column — only the one column may come back.
        var settings = new PgOutboxTableSettings();
        var sql = Create().SqlSelectTenant;

        Assert.Contains(settings.Message.Fields.TenantId, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("*", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlFinishDelivery_HasNoConflictClause()
    {
        // delivery_id is BIGSERIAL, so a primary-key conflict on the log table is impossible
        // and ON CONFLICT here could only ever swallow a bug.
        var sql = ExecutableSql(Create().SqlFinishDelivery(1));

        Assert.DoesNotContain("ON CONFLICT", sql, StringComparison.Ordinal);
        Assert.Contains("RETURNING", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlError_KeepsItsConflictClause()
    {
        // The counter-test to the one above, and the reason this file exists. error_id is a
        // hash of the error text and error_created_at is day-truncated, so the same failure in
        // two batches on one day collides — the clause is the dedup that ErrorDeliveryCommand
        // relies on.
        var sql = ExecutableSql(Create().SqlError(1));

        Assert.Contains("ON CONFLICT DO NOTHING", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlInsertType_KeepsItsConflictClause()
    {
        // type_id is a hash of the type name, same reasoning as SqlError.
        var sql = ExecutableSql(Create().SqlInsertType);

        Assert.Contains("ON CONFLICT DO NOTHING", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlLockAndSelect_UsesNamedWindowParameters()
    {
        var sql = Create().SqlLockAndSelect;

        Assert.Contains(SqlParam.WindowFrom, sql, StringComparison.Ordinal);
        Assert.Contains(SqlParam.RetryAfter, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlLoadConsumerGroup_UsesBothWindowBounds()
    {
        var sql = Create().SqlLoadConsumerGroup;

        Assert.Contains(SqlParam.WindowFrom, sql, StringComparison.Ordinal);
        Assert.Contains(SqlParam.WindowTo, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlCreateOffsetTable_HasLegacyUuidAndLiveSeqCursor()
    {
        // C2 expand: the old group_offset (UUID v7) stays for rolling upgrades and is dropped by
        // a later contract version; the live cursor is group_offset_seq (BIGINT, a msg_seq).
        // Any regression that returns the loader to reading the UUID column must fail here.
        var sql = ExecutableSql(Create().SqlCreateOffsetTable);

        Assert.Contains("group_offset UUID NOT NULL DEFAULT", sql, StringComparison.Ordinal);
        Assert.Contains("group_offset_seq BIGINT NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void OffsetTemplates_ReadAndWriteTheSeqColumn()
    {
        // SqlSelectOffset/SqlUpdateOffset/SqlInitOffset must touch the BIGINT cursor column —
        // the legacy UUID one is write-once-by-old-binaries and never read by this version.
        var builder = Create();

        Assert.Contains("SELECT group_offset_seq\nFROM", ExecutableSql(builder.SqlSelectOffset), StringComparison.Ordinal);
        Assert.Contains("group_offset_seq=@offset", ExecutableSql(builder.SqlUpdateOffset), StringComparison.Ordinal);
        Assert.Contains("group_offset_seq", ExecutableSql(builder.SqlInitOffset), StringComparison.Ordinal);

        Assert.DoesNotContain("SELECT group_offset\nFROM", ExecutableSql(builder.SqlSelectOffset), StringComparison.Ordinal);
        Assert.DoesNotContain("group_offset=", ExecutableSql(builder.SqlUpdateOffset), StringComparison.Ordinal);
    }

    [Fact]
    public void SqlLoadConsumerGroup_CursorLivesOnMsgSeq()
    {
        // C2 regression pin: the load query must select/order/return the database-assigned
        // msg_seq, never the application-minted msg_id. A future "simplification" back to
        // msg_id reopens the skewed-clock message loss the column exists to prevent.
        var sql = ExecutableSql(Create().SqlLoadConsumerGroup);

        Assert.DoesNotContain("msg_id>@offset", sql, StringComparison.Ordinal);
        Assert.Contains("msg_seq>@offset", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY msg_seq", sql, StringComparison.Ordinal);
        Assert.Contains("RETURNING msg_seq", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlLoadConsumerGroup_ReturnsSeqAsNewOffset()
    {
        // The SELECT above the CTE feeds reader.GetInt64(1) in the loader — renaming max_id
        // back to a UUID shape or dropping the DESC LIMIT 1 would be silent corruption here.
        var sql = ExecutableSql(Create().SqlLoadConsumerGroup);

        Assert.Contains("max_seq", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY msg_seq DESC LIMIT 1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlBulkMsgCopy_DoesNotListMsgSeq()
    {
        // msg_seq is BIGSERIAL: COPY omits it deliberately so the database's nextval assigns
        // insertion order. An app-supplied msg_seq would let writer clocks order the cursor.
        var sql = ExecutableSql(Create().SqlBulkMsgCopy);

        Assert.DoesNotContain("msg_seq", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageFields_IncludeMsgSeqAsBigSerial()
    {
        var sql = ExecutableSql(string.Join('\n', new PgOutboxTableSettings().Message.Fields.All()));

        Assert.Contains("msg_seq BIGSERIAL NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskQueueFields_IncludeMsgSeqDenormalized()
    {
        var sql = ExecutableSql(string.Join('\n', new PgOutboxTableSettings().TaskQueue.Fields.All()));

        Assert.Contains("msg_seq BIGINT NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeBlocks_AddMsqSeqAndOffsetSeqIdempotently()
    {
        // C14-style upgrade hook: ADD COLUMN IF NOT EXISTS on msg/task/offset tables, so an
        // existing deployment's schema catches up on the next migration pass and fresh databases
        // treat the statement as a no-op. Must stay idempotent — a re-run may never error.
        var builder = Create();

        Assert.Contains("ADD COLUMN IF NOT EXISTS msg_seq BIGSERIAL NOT NULL", builder.SqlAddMsgSeqColumnToMsgTable, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS msg_seq BIGINT NOT NULL DEFAULT 0", builder.SqlAddMsgSeqColumnToTaskTable, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS group_offset_seq BIGINT NOT NULL DEFAULT 0", builder.SqlAddGroupOffsetSeqColumnToOffsetTable, StringComparison.Ordinal);
    }

    [Fact]
    public void FloorResolvers_SelectMaxSeqBelowTheFloor()
    {
        // WithMinOffset(Guid/DateTimeOffset) floors are translated to an *exclusive* seq boundary:
        // the last message that still sorts below the floor. The loader compares `msg_seq > @offset`
        // against it, so the first message at/above the floor is the first delivered — strict
        // semantics matching the old UUID cursor, no off-by-one. A MIN(...>=floor) would collapse
        // to 0 when nothing qualifies yet and silently deliver everything below the floor.
        var msgIdFloor = Create().SqlResolveMsgIdFloor;
        var dateFloor = Create().SqlResolveDateFloor;
        foreach (var sql in new[] { msgIdFloor, dateFloor })
        {
            Assert.Contains("MAX(", sql, StringComparison.Ordinal);
            Assert.Contains("COALESCE(MAX(", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("MIN(", sql, StringComparison.Ordinal);
        }

        Assert.Contains("<@msg_id", msgIdFloor, StringComparison.Ordinal);
        Assert.Contains("<@flr_date", dateFloor, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlLoadConsumerGroup_InsertsMsgSeqIntoTaskTable()
    {
        // The denormalized task.msg_seq (RETURNING depends on it) must be in the INSERT column
        // list and the SELECT list — "сделай рядом ещё колонку с bigint".
        var sql = ExecutableSql(Create().SqlLoadConsumerGroup);

        // The trailing-comma SELECT list references it as its own column (",msg_seq")…
        Assert.Contains(",msg_seq", sql, StringComparison.Ordinal);
        // …while the INSERT column list carries it immediately after msg_id.
        Assert.Contains("msg_id,\n    msg_seq", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTemplateUsesTheAmbiguousToDateParameter()
    {
        // @to meant three different things depending on which statement was running. It must
        // not come back: window bounds are @win_from/@win_to, the re-rent cutoff is @rty_at.
        // (@frm deliberately survives — it is a genuine lookback floor in SqlExtendDelivery
        // and SqlFinishDelivery, where the lower bound is a retention horizon rather than a
        // window edge. Only the three-way ambiguity is gone.)
        // Asserted against the literal because the constant itself is gone — that is the point.
        foreach (var (name, sql) in AllTemplates(Create()))
        {
            var executable = ExecutableSql(sql);
            Assert.DoesNotContain("@to", executable, StringComparison.Ordinal);
            Assert.True(executable.Trim().Length > 0, name);
        }
    }

    [Fact]
    public void WindowParametersAreNamedAsDocumented()
    {
        // C8: the semantics table in the SqlOutboxBuilder header must match the actual SQL.
        // Each name here is asserted because each one tempted a "cleanup" — collapsing two
        // of them back into @to/@frm reintroduces the ambiguity the rename removed.
        // Concatenating every template (not a hand-picked subset) keeps the window names
        // pinned wherever they actually appear — @win_to lives in SqlLoadConsumerGroup,
        // @win_from in the lock/load selects, @rty_at in SqlLockAndSelect.
        var distinct = new StringBuilder();
        foreach (var (_, sql) in AllTemplates(Create())) distinct.Append(ExecutableSql(sql));

        Assert.Contains("@win_from", distinct.ToString(), StringComparison.Ordinal);
        Assert.Contains("@win_to", distinct.ToString(), StringComparison.Ordinal);
        Assert.Contains("@rty_at", distinct.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("@to", distinct.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BulkStatements_ParameterPerRowRatio_Is8And4_Not16()
    {
        // M8/Q4 ground truth for the batch-size cap. The old SqlCacheSplitter comment claimed
        // "~16 SQL parameters per element" — the actual ratio is 8 indexed params per row and
        // 4 common in SqlFinishDelivery, and 4 per row (no common) in SqlError. A 512-row chunk
        // therefore binds 8×512+4 = 4100 Npgsql parameters in the finish statement and 4×512 =
        // 2048 in the error statement: 6% / 3% of PostgreSQL's 65535-per-statement limit.
        // (That is why we count *distinct* parameter names: @tnt/@gr/@trn are deliberately
        // reused in every VALUES row, which costs nothing — a repeated name is the same bound
        // parameter — but would corrupt a "count of @"-style tally. The old comment counted
        // "elements", and the "~16" figure is wrong by a factor of 2-4.)
        var builder = Create();

        Assert.Equal(4100, CountDistinctParams(ExecutableSql(builder.SqlFinishDelivery(512))));
        Assert.Equal(2048, CountDistinctParams(ExecutableSql(builder.SqlError(512))));
        // Sanity on the ratio itself: the cap is a multiple of the per-row ratio.
        Assert.Equal(8, (CountDistinctParams(ExecutableSql(builder.SqlFinishDelivery(512))) - 4) / 512);
        Assert.Equal(4, CountDistinctParams(ExecutableSql(builder.SqlError(512))) / 512);
    }

    [Fact]
    public void SqlParamWindowNamesAreAsDocumented()
    {
        Assert.Equal("@win_from", SqlParam.WindowFrom);
        Assert.Equal("@win_to", SqlParam.WindowTo);
        Assert.Equal("@rty_at", SqlParam.RetryAfter);
    }

    private static int CountDistinctParams(string sql)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in sql.Split(' ', ',', '(', ')', '=', '<', '>', ';', '\n', '\r'))
        {
            if (token.StartsWith('@')) names.Add(token);
        }
        return names.Count;
    }

    private static (string Name, string Sql)[] AllTemplates(SqlOutboxBuilder b) => new (string, string)[]
    {
        (nameof(b.SqlBulkMsgCopy), b.SqlBulkMsgCopy),
        (nameof(b.SqlLockAndSelect), b.SqlLockAndSelect),
        (nameof(b.SqlExtendDelivery), b.SqlExtendDelivery),
        (nameof(b.SqlCreateTypeTable), b.SqlCreateTypeTable),
        (nameof(b.SqlSelectType), b.SqlSelectType),
        (nameof(b.SqlSelectTenant), b.SqlSelectTenant),
        (nameof(b.SqlInsertType), b.SqlInsertType),
        (nameof(b.SqlCreateOffsetTable), b.SqlCreateOffsetTable),
        (nameof(b.SqlSelectOffset), b.SqlSelectOffset),
        (nameof(b.SqlUpdateOffset), b.SqlUpdateOffset),
        (nameof(b.SqlInitOffset), b.SqlInitOffset),
        (nameof(b.SqlLockOffset), b.SqlLockOffset),
        (nameof(b.SqlLoadConsumerGroup), b.SqlLoadConsumerGroup),
        (nameof(b.SqlResolveMsgIdFloor), b.SqlResolveMsgIdFloor),
        (nameof(b.SqlResolveDateFloor), b.SqlResolveDateFloor),
        (nameof(b.SqlAddMsgSeqColumnToMsgTable), b.SqlAddMsgSeqColumnToMsgTable),
        (nameof(b.SqlAddMsgSeqColumnToTaskTable), b.SqlAddMsgSeqColumnToTaskTable),
        (nameof(b.SqlAddGroupOffsetSeqColumnToOffsetTable), b.SqlAddGroupOffsetSeqColumnToOffsetTable),
        (nameof(b.SqlError), b.SqlError(1)),
        (nameof(b.SqlFinishDelivery), b.SqlFinishDelivery(1)),
    };
}
