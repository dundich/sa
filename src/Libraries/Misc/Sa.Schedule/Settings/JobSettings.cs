namespace Sa.Schedule.Settings;

internal sealed class JobSettings(Type jobType, Guid jobId) : IJobSettings
{
    /// <summary>
    /// handler id
    /// </summary>
    public Guid JobId => jobId;

    public Type JobType => jobType;

    /// <summary>
    /// The keyed DI key of the schedule this job belongs to, or <c>null</c> for the default
    /// (unkeyed) schedule. Used at execution time to resolve the schedule's keyed
    /// <c>IJobErrorHandler</c>; the job's own keyed registration is by <see cref="JobId"/>.
    /// </summary>
    internal string? ScheduleKey { get; set; }

    public JobProperties Properties { get; } = new();

    public JobErrorHandling ErrorHandling { get; } = new();

    IJobProperties IJobSettings.Properties => Properties;

    IJobErrorHandling IJobSettings.ErrorHandling => ErrorHandling;

    internal JobSettings Merge(IJobSettings info)
    {
        // The seed in ScheduleSettings.Create is a fresh instance without a key: absorb it from
        // the first registration so the merged job still knows which schedule owns it.
        if (info is JobSettings source)
        {
            ScheduleKey ??= source.ScheduleKey;
        }

        Properties.Merge(info.Properties);
        ErrorHandling.Merge(info.ErrorHandling);
        return this;
    }


    public static JobSettings Create<T>(Guid jobId)
        where T : class, IJob => new(typeof(T), jobId);

    public static JobSettings Create(IJobSettings settings)
        => new JobSettings(settings.JobType, settings.JobId).Merge(settings);

    public IJobSettings Clone() => new JobSettings(JobType, JobId).Merge(this);
}
