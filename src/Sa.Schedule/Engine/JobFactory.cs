﻿using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule.Settings;

namespace Sa.Schedule.Engine;

internal class JobFactory(IServiceScopeFactory scopeFactory, IInterceptorSettings interceptorSettings, IJobRunner jobRunner) : IJobFactory
{
    public IJobController CreateJobController(IJobSettings settings)
        => new JobController(settings, interceptorSettings, scopeFactory);

    public IJobScheduler CreateJobSchedule(IJobSettings settings)
        => new JobScheduler(jobRunner, CreateJobController(settings));
}
