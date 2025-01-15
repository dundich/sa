using Sa.Timing.Providers;

namespace Sa.Partitional.PostgreSql.Cleaning;

internal class PartCleanupService(
    IPartRepository repository
    , PartCleanupScheduleSettings settings
    , ICurrentTimeProvider timeProvider
    , ISqlBuilder sqlBuilder
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
        DateTimeOffset toDate = timeProvider.GetUtcNow().Add(-settings.DropPartsAfterRetention);
        return Clean(toDate, cancellationToken);
    }
}
