using Sa.Partitional.PostgreSql.Cache;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Migration;

internal sealed class MigrationJob(IMigrationService service, IPartCache cache) : IJob
{
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        await service.Migrate(cancellationToken).ConfigureAwait(false);

        // The migration created partitions behind the cache's back, so its snapshot is
        // stale now. Invalidated unconditionally rather than only on success: Migrate
        // returns -1 both when the lock was never acquired and when it was cancelled
        // midway, and a partial run can still have created partitions before it stopped.
        await cache.RemoveCache().ConfigureAwait(false);
    }
}
