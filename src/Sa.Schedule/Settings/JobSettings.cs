namespace Sa.Schedule.Settings;

internal class JobSettings(Type jobType, Guid jobId) : IJobSettings
{
    /// <summary>
    /// handler id
    /// </summary>
    public Guid JobId { get; } = jobId;

    public Type JobType => jobType;

    public JobProperies Properties { get; } = new();
    public JobErrorHandling ErrorHandling { get; } = new();

    IJobProperties IJobSettings.Properties => Properties;
    IJobErrorHandling IJobSettings.ErrorHandling => ErrorHandling;

    internal JobSettings Merge(IJobSettings info)
    {
        Properties.Merge(info.Properties);
        ErrorHandling.Merge(info.ErrorHandling);
        return this;
    }


    public static JobSettings Create<T>(Guid jobId)
        where T : class, IJob => new(typeof(T), jobId);

    public static JobSettings Create(IJobSettings options)
        => new JobSettings(options.JobType, options.JobId).Merge(options);

    public IJobSettings Clone() => new JobSettings(JobType, JobId).Merge(this);
}
