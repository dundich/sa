using Microsoft.Extensions.Logging;
using Npgsql;
using Sa.Classes;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Partitional.PostgreSql.SqlBuilder;

namespace Sa.Partitional.PostgreSql.Repositories;

internal sealed partial class PartRepository(
    IPgDataSource dataSource,
    ISqlBuilder sqlBuilder,
    ILogger<PartRepository>? logger = null) : IPartRepository, IDisposable
{

    /// <summary>
    /// Semaphore to ensure we don't perform ddl sql concurrently for this data source.
    /// </summary>
    private readonly SemaphoreSlim _migrationSemaphore = new(1, 1);

    public async Task<int> ExecuteDDL(string sql, CancellationToken cancellationToken)
    {
        await _migrationSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await dataSource.ExecuteNonQuery(sql, cancellationToken);
        }
        finally
        {
            _migrationSemaphore.Release();
        }
    }

    public async Task<int> CreatePart(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partValues);

        ISqlTableBuilder builder = sqlBuilder[tableName] ?? throw new KeyNotFoundException(nameof(tableName));
        string sql = builder.CreateSql(date, partValues);
        return await ExecuteDDL(sql, cancellationToken);
    }

    public async Task<int> Migrate(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(resolve);

        int i = 0;

        await foreach (string sql in sqlBuilder.MigrateSql(dates, resolve))
        {
            await ExecuteDDL(sql, cancellationToken);
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
                    return await supMigration.GetParts(cancellationToken);
                }
                else
                {
                    if (tableSettings.PartByListFieldNames.Length > 0)
                    {
                        throw new InvalidOperationException($"Migration support is required for table '{table}' because 'PartByListFieldNames' is specified.");
                    }
                }
            }

            return [];

        }, cancellationToken);

        return i;
    }

    public async Task<List<PartByRangeInfo>> GetPartsFromDate(string tableName, DateTimeOffset fromDate, CancellationToken cancellationToken = default)
    {
        string sql = sqlBuilder.SelectPartsFromDateSql(tableName);
        long unixTime = fromDate.ToUniversalTime().StartOfDay().ToUnixTimeSeconds();

        return await Retry.Jitter(
            async t =>
            {
                try
                {
                    return await dataSource.ExecuteReaderList(
                        sql,
                        ReadPartInfo,
                        [new NpgsqlParameter<long>("from_date", unixTime)], t);
                }
                catch (PostgresException ex) when (UndefinedTable(ex))
                {
                    return [];
                }
            }
            , next: HandleError
            , cancellationToken: cancellationToken);
    }



    public async Task<List<PartByRangeInfo>> GetPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
    {
        string sql = sqlBuilder.SelectPartsToDateSql(tableName);
        try
        {
            return await dataSource.ExecuteReaderList(
                sql
                , ReadPartInfo
                , [new NpgsqlParameter<long>("to_date", toDate.ToUnixTimeSeconds())]
                , cancellationToken);
        }
        catch (PostgresException ex) when (UndefinedTable(ex))
        {
            return [];
        }
    }

    public async Task<int> DropPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default)
    {
        int droppedCount = 0;
        List<PartByRangeInfo> list = await GetPartsToDate(tableName, toDate, cancellationToken);

        LogStartingToDrop(tableName, toDate);

        foreach (PartByRangeInfo part in list)
        {
            ITableSettings? settings = sqlBuilder[part.RootTableName]?.Settings;

            if (settings != null)
            {
                string sql = settings.DropPartSql(part.Id);
                try
                {
                    await ExecuteDDL(sql, cancellationToken);
                    droppedCount++;
                    LogSuccessfullyDropped(part.Id, part.RootTableName);
                }
                catch (PostgresException pgErr) when (UndefinedTable(pgErr))
                {
                    LogSkipToDrop(pgErr, part.Id);
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

    private static bool HandleError(Exception ex, int _ = 0)
    {
        if (ex is PostgresException err)
        {
            return err.SqlState switch
            {
                PostgresErrorCodes.ConnectionException
                 or PostgresErrorCodes.ConnectionFailure
                 or PostgresErrorCodes.DeadlockDetected
                 or PostgresErrorCodes.CannotConnectNow
                   => true, //continue


                _ => false, // abort
            };
        }

        return true;
    }


    private static bool UndefinedTable(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UndefinedTable
        || ex.SqlState == PostgresErrorCodes.InvalidSchemaName
        ;

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
