namespace Sa.Schedule;

/// <summary>
/// Represents the properties of a job.
/// </summary>
public interface IJobProperties
{
    /// <summary>
    /// Gets the name of the job.
    /// </summary>
    string? JobName { get; }

    /// <summary>
    /// Gets a value indicating whether the job should be executed immediately.
    /// </summary>
    bool? Immediate { get; }

    /// <summary>
    /// Gets a value indicating whether the job should run only once.
    /// </summary>
    bool? IsRunOnce { get; }

    /// <summary>
    /// Gets a value indicating whether the job is disabled.
    /// </summary>
    bool? Disabled { get; }

    /// <summary>
    /// Gets the delay before the job starts.
    /// </summary>
    TimeSpan? InitialDelay { get; }

    /// <summary>
    /// Gets the timing configuration for the job.
    /// </summary>
    IJobTiming? Timing { get; }

    /// <summary>
    /// Gets the size of the context stack for the job.
    /// </summary>
    int? ContextStackSize { get; }

    /// <summary>
    /// Gets an optional tag associated with the job.
    /// </summary>
    object? Tag { get; }

    /// <summary>
    /// Gets the current concurrency limit for the job when it starts.
    /// This value cannot exceed <see cref="MaxConcurrency"/>.
    /// Null means use the default limit = 1.
    /// </summary>
    int? ConcurrencyLimit { get; }

    /// <summary>
    /// Gets the absolute maximum concurrency limit allowed for this job.
    /// Null means = ConcurrencyLimit.
    /// </summary>
    int? MaxConcurrency { get; }
}
