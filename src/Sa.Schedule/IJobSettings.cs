using System.Diagnostics.CodeAnalysis;

namespace Sa.Schedule;

/// <summary>
/// Defines the settings for a job.
/// </summary>
public interface IJobSettings
{
    /// <summary>
    /// Gets the unique identifier of the job.
    /// </summary>
    Guid JobId { get; }
    /// <summary>
    /// Gets the type of the job.
    /// </summary>
    Type JobType { get; }
    /// <summary>
    /// Gets the properties of the job.
    /// </summary>
    IJobProperties Properties { get; }
    /// <summary>
    /// Gets the error handling settings for the job.
    /// </summary>
    IJobErrorHandling ErrorHandling { get; }
}