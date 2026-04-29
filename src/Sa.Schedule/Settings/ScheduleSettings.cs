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
