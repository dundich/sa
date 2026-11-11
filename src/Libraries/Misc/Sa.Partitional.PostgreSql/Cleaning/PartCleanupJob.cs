using Sa.Partitional.PostgreSql.Cache;
using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Cleaning;

internal sealed class PartCleanupJob(IPartCleanupService cleaningService, IPartCache cache) : IJob
{
    public async Task ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
    {
        await cleaningService.CleanAsync(cancellationToken).ConfigureAwait(false);

        // Dropped partitions are still listed in the snapshot, which would keep InCache
        // reporting them as present. Unconditional for the same reason as MigrationJob.
        await cache.RemoveCacheAsync().ConfigureAwait(false);
    }
}
