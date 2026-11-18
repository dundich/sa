using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;

namespace Sa.Partitional.PostgreSql.Cleaning;

internal sealed partial class PartCleanupService(
    IPartRepository repository
    , PartCleanupScheduleSettings settings
    , ISqlBuilder sqlBuilder
    , ILogger<PartCleanupService> logger
    , TimeProvider? timeProvider = null) : IPartCleanupService
{
    public async Task<int> CleanAsync(DateTimeOffset toDate, CancellationToken cancellationToken)
    {
        bool archive = settings.CleanupMode == PartCleanupMode.Archive;

        int count = 0;
        Exception? firstError = null;

        foreach (ISqlTableBuilder table in sqlBuilder.Tables)
        {
            string tableName = table.FullName;

            try
            {
                count += archive
                    ? await repository.ArchivePartsToDateAsync(
                        tableName,
                        toDate,
                        settings.ArchiveSchemaName,
                        settings.ArchiveCollisionMode,
                        cancellationToken).ConfigureAwait(false)
                    : await repository.DropPartsToDateAsync(tableName, toDate, cancellationToken).ConfigureAwait(false);

                if (settings.PruneEmptyListParts && table.Settings.PartByListFieldNames.Count > 0)
                {
                    count += await repository.PruneEmptyListPartsAsync(tableName, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The expected stop: the host is shutting down or a deadline elapsed. Retiring
                // partitions is idempotent, so the next run continues where this one stopped.
                throw;
            }
            catch (Exception ex)
            {
                // One broken table must not starve the rest of the module (same rule as the
                // migration). The run as a whole still fails, so the job's error handling sees it.
                LogTableCleanupFailed(ex, tableName);
                firstError ??= ex;
            }
        }

        if (firstError is not null)
        {
            ExceptionDispatchInfo.Capture(firstError).Throw();
        }

        return count;
    }

    public Task<int> CleanAsync(CancellationToken cancellationToken)
    {
        var tp = timeProvider ?? TimeProvider.System;
        DateTimeOffset toDate = tp.GetUtcNow().Add(-settings.DropPartsAfterRetention);
        return CleanAsync(toDate, cancellationToken);
    }

    [LoggerMessage(
        EventId = 207,
        Level = LogLevel.Error,
        Message = "Cleanup of table {TableName} failed. The remaining tables of this run are still processed, then the run fails.")]
    partial void LogTableCleanupFailed(Exception exception, string tableName);
}
