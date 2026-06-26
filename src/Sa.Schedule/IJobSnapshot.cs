namespace Sa.Schedule;

/// <summary>
/// Read-only snapshot of job context properties captured at the time of an error.
/// Designed to be lightweight — avoids cloning the full context stack.
/// </summary>
public interface IJobSnapshot
{
    /// <summary>Gets the job name.</summary>
    string JobName { get; }

    /// <summary>Gets the total number of iterations attempted.</summary>
    ulong NumIterations { get; }

    /// <summary>Gets the number of failed iterations.</summary>
    ulong FailedIterations { get; }

    /// <summary>Gets the number of completed (successful) iterations.</summary>
    ulong CompetedIterations { get; }

    /// <summary>Gets the time when the job was first created.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the time of the last execution, if any.</summary>
    DateTimeOffset? ExecuteAt { get; }

    /// <summary>Gets the number of retry attempts for the current failure.</summary>
    int FailedRetries { get; }

    /// <summary>Gets the message of the last error, if any.</summary>
    string? LastErrorMessage { get; }

    /// <summary>Gets the number of previous context entries on the stack (capped at 10).</summary>
    int StackDepth { get; }
}
