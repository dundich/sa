using Microsoft.Extensions.Logging;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Partitional.PostgreSql.SqlBuilder;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Sa.Partitional.PostgreSql.Repositories;

internal sealed partial class PartRepository(
    IPgDataSource dataSource,
    ISqlBuilder sqlBuilder,
    IPartTableAdopter adopter,
    // Required, not optional: the [LoggerMessage] methods dereference it, so a null logger
    // turned the first drop/migrate into a NullReferenceException.
    ILogger<PartRepository> logger) : IPartRepository, IDisposable
{

    /// <summary>
    /// Semaphore to ensure we don't perform ddl sql concurrently for this data source.
    /// </summary>
    /// <remarks>
    /// Never disposed. <see cref="SemaphoreSlim"/> owns no unmanaged resource unless
    /// <c>AvailableWaitHandle</c> is used (it is not), while disposing it does break a run that is
    /// still queued on it: the host disposes singletons on its way out, and that run would then get
    /// an <see cref="ObjectDisposedException"/> instead of finishing or waiting for its token.
    /// </remarks>
    private readonly SemaphoreSlim _migrationSemaphore = new(1, 1);

    /// <summary>Releases the migration gate.</summary>
    public void Dispose() => _migrationSemaphore.Dispose();

    /// <summary>
    /// SQLSTATE of "object in use" — what PostgreSQL raises when a
    /// <c>DETACH PARTITION CONCURRENTLY</c> left by an interrupted run is still pending.
    /// </summary>
    private const string ObjectInUseSqlState = "55006";

    public async Task<int> ExecuteDDLAsync(string sql, CancellationToken cancellationToken)
    {
        await _migrationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PgRetryStrategy.ExecuteWithRetry(
                async t => await dataSource.ExecuteNonQueryAsync(sql, t).ConfigureAwait(false),
                cancellationToken: cancellationToken);
        }
        finally
        {
            _migrationSemaphore.Release();
        }
    }

    public async Task<int> CreatePartAsync(
        string tableName,
        DateTimeOffset periodStart,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default)
    {
        ISqlTableBuilder builder = sqlBuilder[tableName] ?? throw new KeyNotFoundException(tableName);

        // An adopted table is a plain table until this call converts it; creating a partition on a
        // plain table would fail, and the conversion is a no-op once it has happened.
        await adopter.EnsureAdoptedAsync(tableName, cancellationToken).ConfigureAwait(false);

        string sql = builder.CreateSql(periodStart, partValues);

        return await ExecuteDDLAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MigrateAsync(
        DateTimeOffset[] dates,
        Func<string, Task<StrOrNum[][]>> resolve,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(resolve);

        int statements = 0;
        string? failedTable = null;
        Exception? firstError = null;

        await foreach (MigrateBatch batch in sqlBuilder.MigrateSql(dates, async table =>
        {
            // Convert adopted tables before any partition DDL is generated for them: the generated
            // SQL assumes a partitioned root and never emits its CREATE TABLE in Adopt mode.
            await adopter.EnsureAdoptedAsync(table, cancellationToken).ConfigureAwait(false);

            return await resolve(table).ConfigureAwait(false);
        }).ConfigureAwait(false))
        {
            if (batch.Error is { } resolutionError)
            {
                // Resolving the values of this table (including the adoption step) failed. The other
                // tables still get their turn; the run as a whole fails at the end.
                LogTableMigrationFailed(resolutionError, batch.TableName);
                failedTable = batch.TableName;
                firstError ??= resolutionError;
                continue;
            }

            // The table already failed earlier in this run. The DDL is idempotent, so the next run
            // finishes the remainder; the rest of this table's statements are not attempted.
            if (string.Equals(failedTable, batch.TableName, StringComparison.Ordinal)) continue;

            try
            {
                await ExecuteDDLAsync(batch.Sql!, cancellationToken).ConfigureAwait(false);
                statements++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The run's own deadline (or a manual caller's token) elapsed: that is the expected
                // stop, not a broken table.
                throw;
            }
            catch (Exception ex)
            {
                LogTableMigrationFailed(ex, batch.TableName);
                failedTable = batch.TableName;
                firstError ??= ex;
            }
        }

        if (firstError is not null)
        {
            // Every table had its turn. The run as a whole is still a failure, so the scheduler's
            // error handling sees it (a real failure aborts that job), with the original stack.
            ExceptionDispatchInfo.Capture(firstError).Throw();
        }

        return statements;
    }


    public async Task<int> MigrateAsync(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dates);

        int i = await MigrateAsync(dates, async table =>
        {
            // The built-in settings always carry a migration support; it answers an empty series
            // when no list values were configured, which MigrateSql reports by skipping the table
            // (a list-partitioned table without values has no leaf to create). Only a hand-written
            // ITableSettings may return null here.
            ISqlTableBuilder? builder = sqlBuilder[table];

            if (builder?.Settings.Migration is not { } migration) return [];

            return await migration.GetPartsAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return i;
    }

    public async Task<List<PartByRangeInfo>> GetPartsFromDate(
        string tableName,
        DateTimeOffset fromDate,
        CancellationToken cancellationToken = default)
    {
        string sql = sqlBuilder.SelectPartsFromDateSql(tableName);
        long unixTime = fromDate.ToUniversalTime().StartOfDay().ToUnixTimeSeconds();

        return await GetPartsWithRetry(
            sql,
            "from_date",
            unixTime,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<PartByRangeInfo>> GetPartsToDate(
        string tableName,
        DateTimeOffset toDate,
        CancellationToken cancellationToken = default)
    {
        string sql = sqlBuilder.SelectPartsToDateSql(tableName);

        // Deliberately NOT truncated to the start of the day, unlike GetPartsFromDate. `to_date` in
        // the cache is the exclusive end of a partition range, and the predicate is `to_date <=
        // @to_date`: truncating the cutoff would let a retention run drop the partition that contains
        // the cutoff instant itself.
        //
        // Same retry policy as GetPartsFromDate: both are the same read of the same cache table, and
        // a cleanup that gives up on one transient connection failure would silently skip a batch of
        // partitions to drop.
        return await GetPartsWithRetry(
            sql,
            "to_date",
            toDate.ToUnixTimeSeconds(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the partition cache table, retrying only transport-level failures and treating a missing
    /// cache table as "nothing cached yet" instead of an error.
    /// </summary>
    private async Task<List<PartByRangeInfo>> GetPartsWithRetry(
        string sql,
        string parameterName,
        long unixTime,
        CancellationToken cancellationToken)
    {
        return await PgRetryStrategy.ExecuteWithRetry(
            async t =>
            {
                try
                {
                    return await dataSource.ExecuteReaderListAsync(
                        sql,
                        ReadPartInfo,
                        [new NpgsqlParameter<long>(parameterName, unixTime)], t).ConfigureAwait(false);
                }
                catch (PostgresException ex) when (UndefinedTable(ex))
                {
                    return [];
                }
            }
            , next: CanRetryByError
            , cancellationToken: cancellationToken);
    }

    public async Task<int> DropPartsToDateAsync(
        string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
    {
        int droppedCount = 0;
        List<PartByRangeInfo> list = await GetPartsToDate(tableName, toDate, cancellationToken).ConfigureAwait(false);

        LogStartingToDrop(tableName, toDate);

        foreach (PartByRangeInfo part in list)
        {
            ITableSettings? settings = sqlBuilder[part.RootTableName]?.Settings;

            if (settings != null)
            {
                string sql = settings.DropPartSql(part.Id);
                try
                {
                    await ExecuteDDLAsync(sql, cancellationToken).ConfigureAwait(false);
                    droppedCount++;
                    LogSuccessfullyDropped(part.Id, part.RootTableName);
                }
                catch (PostgresException pgErr) when (UndefinedTable(pgErr))
                {
                    LogSkipToDrop(pgErr, part.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Dropping is idempotent (DROP TABLE IF EXISTS + a DELETE ... WHERE id =), so a
                    // cancelled cleanup can simply stop and let the next run continue. Swallowing the
                    // cancellation here instead would keep the loop spinning over every remaining
                    // partition, failing each one, and then report a completed scan.
                    throw;
                }
                catch (Exception ex)
                {
                    LogFailedToDrop(ex, part.Id);
                }
            }
            else
            {
                LogSkipToDropIfNoSettings(part.RootTableName, part.Id);
            }
        }

        FinishedDropping(droppedCount);

        return droppedCount;
    }

    /// <summary>
    /// Drops list-level partitions of <paramref name="tableName"/> whose whole subtree is empty.
    /// </summary>
    /// <remarks>
    /// The value path is parsed back from the partition name (<c>{root}{sep}{v1}{sep}{v2}…</c>). A name
    /// produced by <see cref="ITableBuilder.WithLongPartitionName"/> no longer carries the values, so
    /// such a partition is left alone — pruning is opt-in and must never guess.
    /// </remarks>
    public async Task<int> PruneEmptyListPartsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        ITableSettings? settings = sqlBuilder[tableName]?.Settings;

        // Only a list-partitioned table has intermediate partitions to prune.
        if (settings is null || settings.PartByListFieldNames.Count == 0) return 0;

        string qualifiedRoot = SqlTemplate.QualifiedName(settings.DatabaseSchemaName, settings.DatabaseTableName);

        // Resolved once, and only for this table: a branch the migration creates must never be pruned,
        // otherwise the cleanup and the next migration tick would fight over it.
        StrOrNum[][] declaredValues = await settings.Migration.GetPartsAsync(cancellationToken).ConfigureAwait(false);
        List<string[]> declared = [.. declaredValues.Select(branch => branch.Select(value => value.ToString()).ToArray())];

        List<ListPartition> parts = await PgRetryStrategy.ExecuteWithRetry(
            async t => await dataSource.ExecuteReaderListAsync(
                SqlTemplate.ListListPartitionsSql(),
                static reader => new ListPartition(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)),
                [new NpgsqlParameter<string>("root", qualifiedRoot)],
                t).ConfigureAwait(false),
            next: CanRetryByError,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string namePrefix = settings.DatabaseTableName + settings.SqlPartSeparator;
        int pruned = 0;

        foreach (ListPartition part in parts)
        {
            if (!part.BareName.StartsWith(namePrefix, StringComparison.Ordinal)) continue;

            string[] path = part.BareName[namePrefix.Length..].Split(settings.SqlPartSeparator);

            // A depth that does not match the number of name segments means the name is not the
            // library's regular form (e.g. a WithLongPartitionName replacement): leave it alone.
            if (path.Length != part.Depth) continue;

            if (IsDeclared(path, declared)) continue;

            string qualified = SqlTemplate.QualifiedName(part.SchemaName, part.BareName);

            try
            {
                if (await TableHasRowsAsync(qualified, cancellationToken).ConfigureAwait(false)) continue;

                await ExecuteDDLAsync(SqlTemplate.DropTableSql(qualified), cancellationToken).ConfigureAwait(false);
                pruned++;
            }
            catch (PostgresException pgErr) when (UndefinedTable(pgErr))
            {
                // Dropped between the listing and the emptiness check (by hand or by another run).
            }
        }

        if (pruned > 0)
        {
            LogPrunedListParts(tableName, pruned);
        }

        return pruned;
    }

    /// <summary>
    /// Reports whether <paramref name="path"/> is a prefix of any branch the table's migration
    /// declares, i.e. whether the branch would be re-created by the next migration tick.
    /// </summary>
    private static bool IsDeclared(string[] path, List<string[]> declared)
    {
        foreach (string[] branch in declared)
        {
            if (branch.Length < path.Length) continue;

            bool match = true;

            for (int i = 0; i < path.Length; i++)
            {
                if (!string.Equals(branch[i], path[i], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match) return true;
        }

        return false;
    }

    private async Task<bool> TableHasRowsAsync(string qualifiedName, CancellationToken cancellationToken)
    {
        List<bool> rows = await PgRetryStrategy.ExecuteWithRetry(
            async t => await dataSource.ExecuteReaderListAsync(
                SqlTemplate.TableHasRowsSql(qualifiedName),
                static reader => reader.GetBoolean(0),
                t).ConfigureAwait(false),
            next: CanRetryByError,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 && rows[0];
    }

    /// <summary>
    /// Archives the partitions of one table that are older than <paramref name="toDate"/>: detaches
    /// each from its parent and moves it — under the same name — into <paramref name="archiveSchemaName"/>,
    /// marking its metadata row as archived. Resumable: a run interrupted between any two steps is
    /// finished by the next one, and an already archived row is not returned by the placement query.
    /// </summary>
    public Task<int> ArchivePartsToDateAsync(
        string tableName,
        DateTimeOffset toDate,
        string archiveSchemaName,
        CancellationToken cancellationToken = default)
        => ArchivePartsToDateAsync(tableName, toDate, archiveSchemaName, ArchiveCollisionMode.Fail, cancellationToken);

    public async Task<int> ArchivePartsToDateAsync(
        string tableName,
        DateTimeOffset toDate,
        string archiveSchemaName,
        ArchiveCollisionMode collisionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveSchemaName);

        ITableSettings? tableSettings = sqlBuilder[tableName]?.Settings;

        if (tableSettings is null)
        {
            LogSkipToArchiveIfNoSettings(tableName);
            return 0;
        }

        List<PartByRangeInfo> list = await GetPartsToDate(tableName, toDate, cancellationToken).ConfigureAwait(false);

        LogStartingToArchive(tableName, toDate, archiveSchemaName);

        // Schema + cache column, once per table: idempotent, and it upgrades a cache table that was
        // created before archiving existed. Runs even with nothing to archive, so the schema is there
        // before the first move.
        await ExecuteDDLAsync(tableSettings.EnsureArchiveSql(archiveSchemaName), cancellationToken).ConfigureAwait(false);

        int archivedCount = 0;

        foreach (PartByRangeInfo part in list)
        {
            ITableSettings? settings = sqlBuilder[part.RootTableName]?.Settings;

            if (settings is null)
            {
                LogSkipToArchiveIfNoSettings(part.RootTableName);
                continue;
            }

            try
            {
                if (await ArchivePartAsync(settings, part, archiveSchemaName, collisionMode, cancellationToken).ConfigureAwait(false))
                {
                    archivedCount++;
                    LogSuccessfullyArchived(part.Id, part.RootTableName);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Same rationale as DropPartsToDate: archiving is resumable, so a cancelled cleanup
                // stops here and lets the next run continue instead of failing every remaining part.
                throw;
            }
            catch (PostgresException pgErr) when (UndefinedTable(pgErr))
            {
                LogSkipToArchive(pgErr, part.Id);
            }
            catch (Exception ex)
            {
                LogFailedToArchive(ex, part.Id);
            }
        }

        FinishedArchiving(archivedCount);

        return archivedCount;
    }

    /// <summary>
    /// Archives a single partition, tolerating the states an interrupted run can leave behind.
    /// </summary>
    /// <returns><see langword="true"/> when the partition ends up archived; otherwise <see langword="false"/>.</returns>
    private async Task<bool> ArchivePartAsync(
        ITableSettings settings,
        PartByRangeInfo part,
        string archiveSchemaName,
        ArchiveCollisionMode collisionMode,
        CancellationToken cancellationToken)
    {
        string archiveQualified = SqlTemplate.QualifiedName(archiveSchemaName, BarePartName(part.Id));

        PartLocation location = await GetPartLocationAsync(settings, part, archiveQualified, cancellationToken).ConfigureAwait(false);

        if (location.InSource && location.InArchive)
        {
            // The archive schema already holds a table with this name (the window was archived
            // earlier, re-created by a backdated write and is due again). SET SCHEMA would fail with
            // duplicate_table *after* the detach, leaving the partition detached from its tree, so
            // the clash is handled before anything is touched.
            if (collisionMode == ArchiveCollisionMode.RenameExisting)
            {
                await RenameArchivedCopyAsync(settings, part, archiveQualified, archiveSchemaName, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                LogArchiveTargetExists(archiveQualified, part.Id);
                return false;
            }
        }

        if (!location.InSource)
        {
            if (location.InArchive)
            {
                // Moved by an earlier run that did not reach the bookkeeping: just finish that.
                await ExecuteDDLAsync(settings.MarkArchivedSql(part.Id), cancellationToken).ConfigureAwait(false);
                return true;
            }

            // The placement query proved the table exists in neither schema (dropped by hand, or a
            // conversion that never completed). Keeping the row makes every later pass warn about
            // the same ghost partition, so the stale metadata is removed instead.
            LogPartNotFound(part.Id);
            await ExecuteDDLAsync(settings.DeletePartCacheRowSql(part.Id), cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (location.Attached)
        {
            try
            {
                await ExecuteDDLAsync(settings.DetachPartSql(part.PartValues.ToArray(), part.Id), cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == ObjectInUseSqlState)
            {
                // A previous DETACH CONCURRENTLY was interrupted and is still pending: no new detach
                // of that parent is allowed until it is finalised.
                LogFinalizeDetach(part.Id);
                await ExecuteDDLAsync(settings.FinalizeDetachPartSql(part.PartValues.ToArray(), part.Id), cancellationToken).ConfigureAwait(false);
            }
        }

        await ExecuteDDLAsync(settings.ArchivePartSql(part.Id, archiveSchemaName), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Moves the already archived copy of a window out of the canonical name, so the partition that
    /// is due again can take it. The copy keeps its data under <c>{name}{separator}prev$N</c>.
    /// </summary>
    /// <remarks>
    /// The table name is not the only schema-global object: its indexes are too, and
    /// <c>ALTER TABLE … RENAME</c> leaves them under their old names. They are renamed alongside,
    /// otherwise moving the new copy into the archive schema fails with <c>42P07 relation …_pkey
    /// already exists</c>.
    /// </remarks>
    private async Task RenameArchivedCopyAsync(
        ITableSettings settings,
        PartByRangeInfo part,
        string archiveQualified,
        string archiveSchemaName,
        CancellationToken cancellationToken)
    {
        string bare = BarePartName(part.Id);

        // Read before the rename: afterwards the old table name resolves to nothing.
        List<string> indexes = await ListTableIndexes(archiveQualified, cancellationToken).ConfigureAwait(false);

        string replacement = await FreeArchivedNameAsync(settings, archiveSchemaName, bare, cancellationToken).ConfigureAwait(false);

        await ExecuteDDLAsync(SqlTemplate.RenameTableSql(archiveQualified, replacement), cancellationToken).ConfigureAwait(false);

        foreach (string index in indexes)
        {
            string indexQualified = SqlTemplate.QualifiedName(archiveSchemaName, index);
            string indexReplacement = await FreeArchivedNameAsync(settings, archiveSchemaName, index, cancellationToken)
                .ConfigureAwait(false);

            await ExecuteDDLAsync(SqlTemplate.RenameIndexSql(indexQualified, indexReplacement), cancellationToken)
                .ConfigureAwait(false);
        }

        LogRenamedArchivedCopy(part.Id, SqlTemplate.QualifiedName(archiveSchemaName, replacement));
    }

    private async Task<List<string>> ListTableIndexes(string qualifiedName, CancellationToken cancellationToken)
    {
        return await PgRetryStrategy.ExecuteWithRetry(
            async t => await dataSource.ExecuteReaderListAsync(
                SqlTemplate.ListTableIndexesSql(),
                static reader => reader.GetString(0),
                [new NpgsqlParameter<string>("table", qualifiedName)],
                t).ConfigureAwait(false),
            next: CanRetryByError,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds a free <c>{name}{separator}prev$N</c> name in the archive schema.
    /// </summary>
    private async Task<string> FreeArchivedNameAsync(
        ITableSettings settings,
        string archiveSchemaName,
        string bareName,
        CancellationToken cancellationToken)
    {
        for (int i = 1; ; i++)
        {
            string suffix = $"{settings.SqlPartSeparator}prev${i.ToString(CultureInfo.InvariantCulture)}";
            string candidate = SqlTemplate.TrimIdentifier(
                bareName, SqlTemplate.MaxIdentifierBytes - Encoding.UTF8.GetByteCount(suffix)) + suffix;

            if (!await TableExistsAsync(SqlTemplate.QualifiedName(archiveSchemaName, candidate), cancellationToken)
                .ConfigureAwait(false))
            {
                return candidate;
            }
        }
    }

    private async Task<bool> TableExistsAsync(string qualifiedName, CancellationToken cancellationToken)
    {
        List<bool> rows = await PgRetryStrategy.ExecuteWithRetry(
            async t => await dataSource.ExecuteReaderListAsync(
                SqlTemplate.TableExistsSql(qualifiedName),
                static reader => reader.GetBoolean(0),
                t).ConfigureAwait(false),
            next: CanRetryByError,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 && rows[0];
    }

    /// <summary>
    /// Reads where a partition currently is (its source schema, the archive schema) and whether it is
    /// still attached to its parent.
    /// </summary>
    private async Task<PartLocation> GetPartLocationAsync(
        ITableSettings settings,
        PartByRangeInfo part,
        string archiveQualifiedName,
        CancellationToken cancellationToken)
    {
        // The leaf hangs off its immediate list parent (values only, no range suffix), which is the
        // root table itself for a range-only table.
        string parentQualified = settings.GetQualifiedTableName(part.PartValues.ToArray());

        string sql = SqlTemplate.PartStateSql(part.Id, archiveQualifiedName, parentQualified);

        List<PartLocation> rows = await PgRetryStrategy.ExecuteWithRetry(
            async t => await dataSource.ExecuteReaderListAsync(
                sql,
                static reader => new PartLocation(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2)),
                t).ConfigureAwait(false),
            next: CanRetryByError,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : default;
    }

    /// <summary>
    /// Extracts the bare table name from a qualified, quoted partition identifier
    /// (<c>"public"."events__y2026m06d26"</c> becomes <c>events__y2026m06d26</c>).
    /// </summary>
    private static string BarePartName(string qualifiedPartName)
        => SqlTemplate.TrySplitQualifiedName(qualifiedPartName, out string[] parts)
            ? parts[^1]
            : qualifiedPartName.Trim('"');

    /// <summary>
    /// Where an archive pass currently finds a partition.
    /// </summary>
    /// <param name="InSource">The table still exists in its original (source) schema.</param>
    /// <param name="InArchive">The table already exists in the archive schema.</param>
    /// <param name="Attached">The table is still attached to its parent.</param>
    private readonly record struct PartLocation(bool InSource, bool InArchive, bool Attached);

    /// <summary>
    /// One intermediate list level of a root table, as read from the catalog.
    /// </summary>
    /// <param name="SchemaName">The schema the partition lives in.</param>
    /// <param name="BareName">The table name without the schema.</param>
    /// <param name="Depth">The nesting depth (1 for the outermost list level).</param>
    private readonly record struct ListPartition(string SchemaName, string BareName, int Depth);

    private static PartByRangeInfo ReadPartInfo(NpgsqlDataReader reader)
    {
        return new PartByRangeInfo
        {
            Id = reader.GetString(0),
            RootTableName = reader.GetString(1),
            // Wrapped: the metadata is read into an array, and the snapshot it lands in must not be
            // mutable through the public `StrOrNum[]`-looking property.
            PartValues = Array.AsReadOnly(SqlTemplate.ParseStrOrNums(reader.GetString(2))),
            PartBy = PgPartBy.FromPartName(reader.GetString(3)),
            FromDate = reader.GetInt64(4).ToDateTimeOffsetFromUnixTimestamp(),
        };
    }

    private static bool CanRetryByError(Exception ex, int _ = 0)
        => PgErrorCodes.CanRetryByError(ex);


    private static bool UndefinedTable(PostgresException ex) => PgErrorCodes.IsUndefinedTable(ex);


    [LoggerMessage(
    EventId = 101,
    Level = LogLevel.Information,
    Message = "Starting to drop parts for table {TableName} up to date {ToDate}.")]
    partial void LogStartingToDrop(string tableName, DateTimeOffset toDate);

    [LoggerMessage(
        EventId = 202,
        Level = LogLevel.Information,
        Message = "Successfully dropped part with ID {PartId} from table {TableName}.")]
    partial void LogSuccessfullyDropped(string partId, string tableName);

    [LoggerMessage(
        EventId = 403,
        Level = LogLevel.Warning,
        Message = "Skip to drop part with ID {PartId}.")]
    partial void LogSkipToDrop(Exception exception, string partId);

    [LoggerMessage(
        EventId = 501,
        Level = LogLevel.Error,
        Message = "Failed to drop part with ID {PartId}.")]
    partial void LogFailedToDrop(Exception exception, string partId);


    [LoggerMessage(
        EventId = 203,
        Level = LogLevel.Information,
        Message = "Finished dropping parts. Total dropped: {DroppedCount}.")]
    partial void FinishedDropping(int droppedCount);


    [LoggerMessage(
        EventId = 102,
        Level = LogLevel.Debug,
        Message = "No settings found for root table {RootTableName}. Skipping part with ID {PartId}")]
    partial void LogSkipToDropIfNoSettings(string rootTableName, string partId);

    [LoggerMessage(
        EventId = 104,
        Level = LogLevel.Error,
        Message = "Migration of table {TableName} failed. The remaining tables of this run are still processed, then the run fails.")]
    partial void LogTableMigrationFailed(Exception exception, string tableName);

    [LoggerMessage(
        EventId = 204,
        Level = LogLevel.Information,
        Message = "Starting to archive parts for table {TableName} up to date {ToDate} into schema {ArchiveSchema}.")]
    partial void LogStartingToArchive(string tableName, DateTimeOffset toDate, string archiveSchema);

    [LoggerMessage(
        EventId = 205,
        Level = LogLevel.Information,
        Message = "Successfully archived part with ID {PartId} from table {TableName}.")]
    partial void LogSuccessfullyArchived(string partId, string tableName);

    [LoggerMessage(
        EventId = 206,
        Level = LogLevel.Information,
        Message = "Finished archiving parts. Total archived: {ArchivedCount}.")]
    partial void FinishedArchiving(int archivedCount);

    [LoggerMessage(
        EventId = 209,
        Level = LogLevel.Information,
        Message = "Pruned {PrunedCount} empty list-level partition(s) of table {TableName}.")]
    partial void LogPrunedListParts(string tableName, int prunedCount);

    [LoggerMessage(
        EventId = 407,
        Level = LogLevel.Warning,
        Message = "Skip to archive part with ID {PartId}.")]
    partial void LogSkipToArchive(Exception exception, string partId);

    [LoggerMessage(
        EventId = 408,
        Level = LogLevel.Warning,
        Message = "Part with ID {PartId} was not found in its source or archive schema; removing its stale cache row.")]
    partial void LogPartNotFound(string partId);

    [LoggerMessage(
        EventId = 409,
        Level = LogLevel.Information,
        Message = "A previous detach of part with ID {PartId} is pending; finalising it before archiving.")]
    partial void LogFinalizeDetach(string partId);

    [LoggerMessage(
        EventId = 410,
        Level = LogLevel.Warning,
        Message = "Archive target {ArchiveTable} already exists for part with ID {PartId}. The part is left attached; rename or drop the archived copy to let the new one in.")]
    partial void LogArchiveTargetExists(string archiveTable, string partId);

    [LoggerMessage(
        EventId = 411,
        Level = LogLevel.Warning,
        Message = "Archived copy of part with ID {PartId} was renamed to {RenamedTable} to free the canonical archive name.")]
    partial void LogRenamedArchivedCopy(string partId, string renamedTable);

    [LoggerMessage(
        EventId = 502,
        Level = LogLevel.Error,
        Message = "Failed to archive part with ID {PartId}.")]
    partial void LogFailedToArchive(Exception exception, string partId);

    [LoggerMessage(
        EventId = 103,
        Level = LogLevel.Debug,
        Message = "No settings found for root table {RootTableName}. Skipping archiving.")]
    partial void LogSkipToArchiveIfNoSettings(string rootTableName);
}
