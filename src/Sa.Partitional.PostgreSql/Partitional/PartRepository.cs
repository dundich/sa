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
}
