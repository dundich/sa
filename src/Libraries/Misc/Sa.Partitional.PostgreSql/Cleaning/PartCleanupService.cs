namespace Sa.Partitional.PostgreSql.Cleaning;

internal sealed class PartCleanupService(
    IPartRepository repository
    , PartCleanupScheduleSettings settings
    , ISqlBuilder sqlBuilder
    , TimeProvider? timeProvider = null) : IPartCleanupService
{
    public async Task<int> Clean(DateTimeOffset toDate, CancellationToken cancellationToken)
    {
        int cnt = 0;
        bool archive = settings.CleanupMode == PartCleanupMode.Archive;

        foreach (ISqlTableBuilder table in sqlBuilder.Tables)
        {
            string tableName = table.FullName;

            cnt += archive
                ? await repository.ArchivePartsToDate(
                    tableName, toDate, settings.ArchiveSchemaName, cancellationToken).ConfigureAwait(false)
                : await repository.DropPartsToDate(tableName, toDate, cancellationToken).ConfigureAwait(false);
        }

        return cnt;
    }

    public Task<int> Clean(CancellationToken cancellationToken)
    {
        var tp = timeProvider ?? TimeProvider.System;
        DateTimeOffset toDate = tp.GetUtcNow().Add(-settings.DropPartsAfterRetention);
        return Clean(toDate, cancellationToken);
    }
}
