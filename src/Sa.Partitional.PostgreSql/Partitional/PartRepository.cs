using Microsoft.Extensions.Logging;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSql.Repositories;

internal sealed partial class PartRepository(
    IPgDataSource dataSource,
    ISqlBuilder sqlBuilder,
    // Required, not optional: the [LoggerMessage] methods dereference it, so a null logger
    // turned the first drop/migrate into a NullReferenceException.
    ILogger<PartRepository> logger) : IPartRepository, IDisposable
{

    /// <summary>
    /// Semaphore to ensure we don't perform ddl sql concurrently for this data source.
    /// </summary>
    private readonly SemaphoreSlim _migrationSemaphore = new(1, 1);

    /// <summary>
    /// SQLSTATE of "object in use" — what PostgreSQL raises when a
    /// <c>DETACH PARTITION CONCURRENTLY</c> left by an interrupted run is still pending.
    /// </summary>
    private const string ObjectInUseSqlState = "55006";

    public async Task<int> ExecuteDDL(string sql, CancellationToken cancellationToken)
    {
        await _migrationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PgRetryStrategy.ExecuteWithRetry(
                async t => await dataSource.ExecuteNonQuery(sql, t).ConfigureAwait(false),
                cancellationToken: cancellationToken);
        }
        finally
        {
            _migrationSemaphore.Release();
        }
    }

    public async Task<int> CreatePart(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default)
    {
        ISqlTableBuilder builder = sqlBuilder[tableName] ?? throw new KeyNotFoundException(tableName);
        string sql = builder.CreateSql(date, partValues);

        return await ExecuteDDL(sql, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> Migrate(
        DateTimeOffset[] dates,
        Func<string, Task<StrOrNum[][]>> resolve,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(resolve);

        int i = 0;

        await foreach (string sql in sqlBuilder.MigrateSql(dates, resolve).ConfigureAwait(false))
        {
            await ExecuteDDL(sql, cancellationToken).ConfigureAwait(false);
            i++;
        }
        return i;
    }


    public async Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dates);
        int i = await Migrate(dates, async table =>
        {
            ITableSettings? tableSettings = sqlBuilder[table]?.Settings;

            if (tableSettings != null)
            {
                IPartTableMigrationSupport? supMigration = tableSettings.Migration;

                if (supMigration != null)
                {
                    return await supMigration.GetParts(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (tableSettings.PartByListFieldNames.Length > 0)
                    {
                        throw new InvalidOperationException(
                            $"Migration support is required for table '{table}' because 'PartByListFieldNames' is specified.");
                    }
                }
            }

            return [];

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
                    return await dataSource.ExecuteReaderList(
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

    public async Task<int> DropPartsToDate(
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
                    await ExecuteDDL(sql, cancellationToken).ConfigureAwait(false);
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
    /// Archives the partitions of one table that are older than <paramref name="toDate"/>: detaches
    /// each from its parent and moves it — under the same name — into <paramref name="archiveSchemaName"/>,
    /// marking its metadata row as archived. Resumable: a run interrupted between any two steps is
    /// finished by the next one, and an already archived row is not returned by the placement query.
    /// </summary>
    public async Task<int> ArchivePartsToDate(
        string tableName,
        DateTimeOffset toDate,
        string archiveSchemaName,
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
        await ExecuteDDL(tableSettings.EnsureArchiveSql(archiveSchemaName), cancellationToken).ConfigureAwait(false);

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
                if (await ArchivePart(settings, part, archiveSchemaName, cancellationToken).ConfigureAwait(false))
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
    private async Task<bool> ArchivePart(
        ITableSettings settings,
        PartByRangeInfo part,
        string archiveSchemaName,
        CancellationToken cancellationToken)
    {
        string archiveQualified = SqlTemplate.QualifiedName(archiveSchemaName, BarePartName(part.Id));

        PartLocation location = await GetPartLocation(settings, part, archiveQualified, cancellationToken).ConfigureAwait(false);

        if (!location.InSource)
        {
            if (location.InArchive)
            {
                // Moved by an earlier run that did not reach the bookkeeping: just finish that.
                await ExecuteDDL(settings.MarkArchivedSql(part.Id), cancellationToken).ConfigureAwait(false);
                return true;
            }

            LogPartNotFound(part.Id);
            return false;
        }

        if (location.Attached)
        {
            try
            {
                await ExecuteDDL(settings.DetachPartSql(part.PartValues, part.Id), cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == ObjectInUseSqlState)
            {
                // A previous DETACH CONCURRENTLY was interrupted and is still pending: no new detach
                // of that parent is allowed until it is finalised.
                LogFinalizeDetach(part.Id);
                await ExecuteDDL(settings.FinalizeDetachPartSql(part.PartValues, part.Id), cancellationToken).ConfigureAwait(false);
            }
        }

        await ExecuteDDL(settings.ArchivePartSql(part.Id, archiveSchemaName), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reads where a partition currently is (its source schema, the archive schema) and whether it is
    /// still attached to its parent.
    /// </summary>
    private async Task<PartLocation> GetPartLocation(
        ITableSettings settings,
        PartByRangeInfo part,
        string archiveQualifiedName,
        CancellationToken cancellationToken)
    {
        // The leaf hangs off its immediate list parent (values only, no range suffix), which is the
        // root table itself for a range-only table.
        string parentQualified = settings.GetQualifiedTableName(part.PartValues.AsSpan());

        string sql = SqlTemplate.PartStateSql(part.Id, archiveQualifiedName, parentQualified);

        List<PartLocation> rows = await PgRetryStrategy.ExecuteWithRetry(
            async t => await dataSource.ExecuteReaderList(
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
    {
        int separator = qualifiedPartName.LastIndexOf("\".\"", StringComparison.Ordinal);
        string tail = separator < 0 ? qualifiedPartName : qualifiedPartName[(separator + 3)..];
        return tail.Trim('"');
    }

    /// <summary>
    /// Where an archive pass currently finds a partition.
    /// </summary>
    /// <param name="InSource">The table still exists in its original (source) schema.</param>
    /// <param name="InArchive">The table already exists in the archive schema.</param>
    /// <param name="Attached">The table is still attached to its parent.</param>
    private readonly record struct PartLocation(bool InSource, bool InArchive, bool Attached);

    private static PartByRangeInfo ReadPartInfo(NpgsqlDataReader reader)
    {
        return new PartByRangeInfo(
            reader.GetString(0)
            , reader.GetString(1)
            , SqlTemplate.ParseStrOrNums(reader.GetString(2))
            , PgPartBy.FromPartName(reader.GetString(3))
            , reader.GetInt64(4).ToDateTimeOffsetFromUnixTimestamp()
        );
    }

    private static bool CanRetryByError(Exception ex, int _ = 0)
        => PgErrorCodes.CanRetryByError(ex);


    private static bool UndefinedTable(PostgresException ex) => PgErrorCodes.IsUndefinedTable(ex);

    public void Dispose()
    {
        _migrationSemaphore.Dispose();
    }


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
        EventId = 407,
        Level = LogLevel.Warning,
        Message = "Skip to archive part with ID {PartId}.")]
    partial void LogSkipToArchive(Exception exception, string partId);

    [LoggerMessage(
        EventId = 408,
        Level = LogLevel.Warning,
        Message = "Part with ID {PartId} was not found in its source or archive schema. Skipping.")]
    partial void LogPartNotFound(string partId);

    [LoggerMessage(
        EventId = 409,
        Level = LogLevel.Information,
        Message = "A previous detach of part with ID {PartId} is pending; finalising it before archiving.")]
    partial void LogFinalizeDetach(string partId);

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
