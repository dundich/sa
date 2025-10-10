using Sa.Schedule;

namespace Sa.Partitional.PostgreSql.Migration;

internal sealed class PartMigrationJob(IPartMigrationService service) : IJob
{
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        await service.Migrate(cancellationToken);
    }
}
