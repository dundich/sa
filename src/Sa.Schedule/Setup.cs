using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.Schedule;

/// <summary>
/// Provides extension methods for setting up the scheduling system.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Adds the scheduling system to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add the scheduling system to.</param>
    /// <param name="configure">An action to configure the scheduling system.</param>
    /// <returns>The service collection with the scheduling system added.</returns>
    public static IServiceCollection AddSchedule(this IServiceCollection services, Action<IScheduleBuilder> configure)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IScheduler, Scheduler>();
        services.TryAddSingleton<IJobFactory, JobFactory>();
        services.TryAddSingleton<IJobRunner, JobRunner>();
        services.TryAddSingleton<IJobErrorHandler, JobErrorHandler>();

        configure.Invoke(new ScheduleBuilder(services));
        return services;
    }
}