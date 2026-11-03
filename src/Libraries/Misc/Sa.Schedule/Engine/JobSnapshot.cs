namespace Sa.Schedule.Engine;

/// <summary>
/// Read-only snapshot of job context properties, captured at construction time.
/// Instances are pushed onto the context stack and also serve as
/// <see cref="JobException.ContextSnapshot"/>.
/// </summary>
internal sealed class JobSnapshot(IJobContext context) : IJobSnapshot
{
    public string JobName { get; } = context.JobName;

    public ulong NumIterations { get; } = context.NumIterations;

    public ulong FailedIterations { get; } = context.FailedIterations;

    public ulong CompletedIterations { get; } = context.CompletedIterations;

    public DateTimeOffset CreatedAt { get; } = context.CreatedAt;

    public DateTimeOffset? ExecuteAt { get; } = context.ExecuteAt;

    public int FailedRetries { get; } = context.FailedRetries;

    /// <summary>
    /// The message of the underlying error of the previous failure
    /// (<see cref="JobException.InnerException"/>), if any.
    /// </summary>
    public string? LastErrorMessage { get; } = context.LastError?.InnerException?.Message;

    /// <summary>
    /// The number of previous context entries on the stack (capped at 10).
    /// </summary>
    public int StackDepth { get; } = CountStack(context);

    private static int CountStack(IJobContext context)
    {
        int count = 0;
        foreach (var _ in context.Stack)
        {
            count++;
            if (count >= 10) break;
        }
        return count;
    }
}
