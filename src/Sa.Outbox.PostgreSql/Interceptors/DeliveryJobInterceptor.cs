using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSql.Interceptors;

internal sealed class DeliveryJobInterceptor(IMigrationService migrationService) : IOutboxDeliveryJobInterceptor
{
    public async Task OnHandle(
        IJobContext context,
        Func<Task> next,
        object? key,
        CancellationToken cancellationToken)
    {
        if (!migrationService.OnMigrated.IsCancellationRequested
            && context.Settings.JobType.Name.StartsWith("DeliveryJob"))
        {
            return;
        }

        await next();
    }
}
