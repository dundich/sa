using System.Diagnostics.CodeAnalysis;

namespace Sa.Schedule.Settings;

internal class ScheduleSettings : IScheduleSettings
{
    private Dictionary<Guid, JobSettings> _storage = [];

    public bool IsHostedService { get; private set; }

    public Func<IJobContext, Exception, bool>? HandleError { get; private set; }

    public virtual IEnumerable<IJobSettings> GetJobSettings() => _storage.Values.Where(c => c.Properties.Disabled != true);

    public void UseHostedService() => IsHostedService = true;

    internal static ScheduleSettings Create(IEnumerable<JobSettings> jobSettings, bool isHostedService, Func<IJobContext, Exception, bool>? handleError)
    {
        IEnumerable<JobSettings> items = jobSettings.GroupBy(
            c => (c.JobId, c.JobType)
            , (k, items) => items.Aggregate(seed: new JobSettings(k.JobType, k.JobId), (s1, s2) => s1.Merge(s2))
        );

        return new ScheduleSettings
        {
            HandleError = handleError,
            IsHostedService = isHostedService,
            _storage = items.ToDictionary(c => c.JobId)
        };
    }
}
