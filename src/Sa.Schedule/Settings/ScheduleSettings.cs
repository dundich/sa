using Microsoft.Extensions.DependencyInjection;

namespace Sa.Schedule.Settings;

internal sealed class ScheduleSettings : IScheduleSettings
{
    private readonly IReadOnlyDictionary<Guid, JobSettings> _storage;

    private ScheduleSettings(
        IReadOnlyDictionary<Guid, JobSettings> storage,
        Func<IJobContext, Exception, bool>? handleError,
        bool isHostedService)
    {
        _storage = storage;
        IsHostedService = isHostedService;
        HandleError = handleError;
    }

    public bool IsHostedService { get; }

    public Func<IJobContext, Exception, bool>? HandleError { get; }

    public IEnumerable<IJobSettings> GetJobSettings()
        => _storage.Values.Where(c => c.Properties.Disabled != true);

    /// <summary>
    /// Builds the settings from everything registered in the container.
    /// </summary>
    internal static ScheduleSettings Create(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Return true = "the error is consumed" (see JobErrorHandler), so several handlers
        // compose as "any of them consumed it". With a single handler this is that handler,
        // unchanged — no wrapper, no extra indirection on the error path.
        Func<IJobContext, Exception, bool>? handleError =
            provider.GetServices<ErrorHandlerRegistration>()
                .Select(c => c.Handler)
                .ToArray() switch
            {
                [] => null,
                [var single] => single,
                var all => (IJobContext context, Exception exception)
                    => all.Any(handler => handler(context, exception)),
            };

        return Create(
            provider.GetServices<JobSettings>(),
            isHostedService: provider.GetService<ScheduleHostedServiceMarker>() is not null,
            handleError: handleError);
    }

    internal static ScheduleSettings Create(
        IEnumerable<JobSettings> jobSettings,
        bool isHostedService,
        Func<IJobContext, Exception, bool>? handleError)
    {
        IEnumerable<JobSettings> items = jobSettings.GroupBy(
            c => (c.JobId, c.JobType)
            , (k, items) => items.Aggregate(
                seed: new JobSettings(k.JobType, k.JobId),
                (s1, s2) => s1.Merge(s2))
        );

        return new ScheduleSettings(
            storage: items.ToDictionary(c => c.JobId),
            handleError: handleError,
            isHostedService: isHostedService
        );
    }
}
