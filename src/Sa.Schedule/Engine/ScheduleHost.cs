using Microsoft.Extensions.Hosting;

namespace Sa.Schedule.Engine;

internal sealed class ScheduleHost(IScheduler controller) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await controller.Start(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await controller.Stop();
    }
}
