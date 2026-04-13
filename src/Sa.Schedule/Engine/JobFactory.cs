using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule.Settings;

namespace Sa.Schedule.Engine;

internal sealed class JobFactory(
    IServiceScopeFactory scopeFactory,
    IInterceptorSettings interceptorSettings,
    IJobRunner jobRunner,
    TimeProvider? timeProvider = null) : IJobFactory
{
    public IJobScheduler CreateJobSchedule(IJobSettings settings)
        => new JobScheduler(settings, jobRunner, () => CreateController(settings));

    private JobController CreateController(IJobSettings settings)
    {
        return new(
            settings,
            interceptorSettings,
            scopeFactory,
            timeProvider ?? TimeProvider.System);
    }
}
