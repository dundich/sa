using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSql.Interceptors;

internal sealed class DeliveryJobInterceptor(IMigrationService migrationService) : IOutboxDeliveryJobInterceptor
{
    public async Task OnHandleAsync(
        IJobContext context,
        Func<Task> continuation,
        object? key,
        CancellationToken cancellationToken)
    {
        if (!migrationService.OnMigrated.IsCancellationRequested
            && context.Settings.JobType.Name.StartsWith("DeliveryJob", StringComparison.Ordinal))
        {
            return;
        }

        await continuation();
    }
}
