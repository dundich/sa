using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        var settings = Create(
            provider.GetServices<JobSettings>(),
            isHostedService: provider.GetService<ScheduleHostedServiceMarker>() is not null,
            handleError: handleError);

        // Applied after the (JobId, JobType) merge, so configuration lands on the single
        // merged instance and wins over code in every field — including re-enabling a job
        // disabled in code. The .Value read runs the validators, so invalid options surface
        // here as an OptionsValidationException, not deep in a job run.
        settings.ApplyConfiguration(
            provider.GetService<IOptions<ScheduleOptions>>()?.Value);

        return settings;
    }

    internal void ApplyConfiguration(ScheduleOptions? options)
    {
        if (options is null)
        {
            return;
        }

        // The schedule-wide zone default is validated alongside the per-job ones by the
        // options validator, so this resolve runs only for ids it already accepted.
        TimeZoneInfo? defaultTimeZone = options.TimeZone is null
            ? null
            : TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);

        foreach (var job in _storage.Values)
        {
            // JobName (WithName(...)) when set, otherwise the type's full name — the same
            // key the configuration section is looked up by.
            var key = job.Properties.JobName is { Length: > 0 } name
                ? name
                : job.JobType.FullName ?? string.Empty;

            // jobOptions may be null (no section for this job) — the schedule-wide zone
            // default must still reach jobs without their own section.
            options.Jobs.TryGetValue(key, out var jobOptions);

            job.Properties.ApplyConfiguration(jobOptions, defaultTimeZone);
        }
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
