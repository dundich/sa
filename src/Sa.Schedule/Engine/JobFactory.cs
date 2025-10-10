using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule.Settings;

namespace Sa.Schedule.Engine;

internal sealed class JobFactory(
    IServiceScopeFactory scopeFactory, 
    IInterceptorSettings interceptorSettings, 
    IJobRunner jobRunner, 
    TimeProvider timeProvider) : IJobFactory
{
    public IJobController CreateJobController(IJobSettings settings)
        => new JobController(settings, interceptorSettings, scopeFactory, timeProvider);

    public IJobScheduler CreateJobSchedule(IJobSettings settings)
        => new JobScheduler(jobRunner, CreateJobController(settings));
}
