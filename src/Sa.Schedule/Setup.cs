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
    /// <param name="configure">
    /// An action to configure the scheduling system. Invoked with a fresh
    /// <see cref="IScheduleBuilder"/> per call, so jobs from every call end up in the same schedule.
    /// </param>
    /// <returns>The <see cref="IServiceCollection"/> with the scheduling system added.</returns>
    /// <remarks>
    /// Safe to call more than once: the core services are registered with <c>TryAdd</c> and jobs
    /// accumulate, which is how libraries that contribute jobs compose
    /// (<c>AddSaPartitional</c> adds migration + cleanup, one <c>AddDeliveryJob</c> per consumer
    /// group). Global settings added through <see cref="IScheduleBuilder.AddErrorHandler"/> and
    /// <see cref="IScheduleBuilder.UseHostedService"/> accumulate as well — they are stored in
    /// the container, not on the builder.
    /// <para>
    /// Every call must complete before the first <see cref="IScheduler"/> is resolved: the
    /// scheduler is a singleton and snapshots <see cref="IScheduleSettings"/> when constructed,
    /// so jobs registered afterwards are silently ignored.
    /// </para>
    /// <para>
    /// <see cref="TimeProvider"/> is registered as <see cref="TimeProvider.System"/> when absent,
    /// so a test can register its own before building the provider.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaSchedule(
        this IServiceCollection services,
        Action<IScheduleBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered before `configure` so a caller can Replace any of them from inside the
        // callback — TryAdd would not undo a later Add, and Replace needs a prior registration.
        AddSaScheduleCore(services);

        configure?.Invoke(new ScheduleBuilder(services));

        return services;
    }

    private static void AddSaScheduleCore(IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IScheduler, Scheduler>();
        services.TryAddSingleton<IJobFactory, JobFactory>();
        services.TryAddSingleton<IJobRunner, JobRunner>();
        services.TryAddSingleton<IJobErrorHandler, JobErrorHandler>();

        // Not a side effect of `new ScheduleBuilder(...)` — the settings must exist even when
        // no builder is created, or a call to AddSaSchedule() without `configure` would leave
        // the scheduler unresolvable.
        ScheduleSettingsRegistration.AddTo(services);
    }
}
