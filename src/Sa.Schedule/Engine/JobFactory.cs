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
        => new JobScheduler(
            settings,
            jobRunner,
            i => CreateController(i, settings));

    private JobController CreateController(int index, IJobSettings settings)
    {
        return new(
            index,
            settings,
            interceptorSettings,
            scopeFactory,
            timeProvider ?? TimeProvider.System);
    }
}
