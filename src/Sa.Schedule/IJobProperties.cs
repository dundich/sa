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
}
