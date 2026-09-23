using Microsoft.Extensions.Hosting;

namespace Sa.Schedule.Engine;

internal sealed class ScheduleHost(IScheduler scheduler) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await scheduler.Start(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await scheduler.Stop();
    }
}
