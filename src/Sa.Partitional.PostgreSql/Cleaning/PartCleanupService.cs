namespace Sa.Partitional.PostgreSql.Cleaning;

internal class PartCleanupService(
    IPartRepository repository
    , PartCleanupScheduleSettings settings
    , ISqlBuilder sqlBuilder
    , TimeProvider? timeProvider = null
) : IPartCleanupService
{
    public async Task<int> Clean(DateTimeOffset toDate, CancellationToken cancellationToken)
    {
        int cnt = 0;
        foreach (string tableName in sqlBuilder.Tables.Select(c => c.FullName))
        {
            cnt += await repository.DropPartsToDate(tableName, toDate, cancellationToken);
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
