using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Schedule.Settings;

/// <summary>
/// Marks that at least one builder asked for the Generic Host integration.
/// </summary>
internal sealed class ScheduleHostedServiceMarker
{
    public static readonly ScheduleHostedServiceMarker Instance = new();

    private ScheduleHostedServiceMarker() { }
}

/// <summary>
/// A single global error handler registered through <see cref="IScheduleBuilder.AddErrorHandler"/>.
/// </summary>
internal sealed record ErrorHandlerRegistration(Func<IJobContext, Exception, bool> Handler);

/// <summary>
/// Registers the schedule settings singletons.
/// </summary>
internal static class ScheduleSettingsRegistration
{
    /// <summary>
    /// Registers <see cref="IScheduleSettings"/> and <see cref="IInterceptorSettings"/> for the
    /// default (unkeyed) schedule, or — with a schedule key — for a keyed schedule.
    /// </summary>
    /// <remarks>
    /// Idempotent — the first registration wins. That is exactly why neither factory may
    /// capture per-builder state: <c>AddSaSchedule</c> is called once per library that
    /// contributes jobs (<c>AddSaPartitional</c> registers migration + cleanup, one
    /// <c>AddDeliveryJob</c> per consumer group), and every builder after the first is
    /// discarded. Global settings therefore live in the container, not on the builder. Each
    /// keyed schedule has its own instances, collected under its own key.
    /// </remarks>
    public static void AddTo(IServiceCollection services, string? scheduleKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (scheduleKey is null)
        {
            services.TryAddSingleton<IScheduleSettings>(
                static sp => ScheduleSettings.Create(sp));

            services.TryAddSingleton<IInterceptorSettings>(
                static sp => new InterceptorSettings(sp.GetServices<JobInterceptorSettings>()));
        }
        else
        {
            services.TryAddKeyedSingleton<IScheduleSettings>(
                scheduleKey, static (sp, key) => ScheduleSettings.Create(sp, (string)key!));

            services.TryAddKeyedSingleton<IInterceptorSettings>(
                scheduleKey, static (sp, key) =>
                    new InterceptorSettings(sp.GetKeyedServices<JobInterceptorSettings>(key)));
        }
    }
}
