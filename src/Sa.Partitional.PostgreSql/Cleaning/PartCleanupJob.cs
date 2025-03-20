using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Cleaning;

internal class PartCleanupJob(IPartCleanupService cleaningService) : IJob
{
    public Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        return cleaningService.Clean(cancellationToken);
    }
}
