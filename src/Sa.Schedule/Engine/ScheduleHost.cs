using Microsoft.Extensions.Hosting;

namespace Sa.Schedule.Engine;

internal sealed class ScheduleHost(IScheduler controller) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        controller.Start(cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await controller.Stop();
    }
}
