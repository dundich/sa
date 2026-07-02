namespace Sa.Schedule;

/// <summary>
/// Represents an error that occurred during job execution.
/// </summary>
/// <param name="context">The job context at the time of the error.</param>
/// <param name="innerException">The underlying exception that caused this error.</param>
public class JobException(IJobContext context, Exception? innerException)
    : Exception($"[{context.JobName}] job error", innerException)
{
    /// <summary>
    /// Gets a lightweight snapshot of the job context at the time of the error.
    /// Contains only scalar properties and stack depth (capped at 10).
    /// Avoids cloning the full context stack.
    /// </summary>
    public IJobSnapshot ContextSnapshot { get; } = new JobSnapshot(context);

    private sealed class JobSnapshot : IJobSnapshot
    {
        public string JobName { get; }
        public ulong NumIterations { get; }
        public ulong FailedIterations { get; }
        public ulong CompetedIterations { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? ExecuteAt { get; }
        public int FailedRetries { get; }
        public string? LastErrorMessage { get; }
        public int StackDepth { get; }

        public JobSnapshot(IJobContext context)
        {
            JobName = context.JobName;
            NumIterations = context.NumIterations;
            FailedIterations = context.FailedIterations;
            CompetedIterations = context.CompetedIterations;
            CreatedAt = context.CreatedAt;
            ExecuteAt = context.ExecuteAt;
            FailedRetries = context.FailedRetries;
            LastErrorMessage = context.LastError?.Message;

            // Count stack depth without cloning (cap at 10)
            var count = 0;
            if (context.Stack != null)
            {
                foreach (var _entry in context.Stack)
                {
                    count++;
                    if (count >= 10) break;
                }
            }
            StackDepth = count;
        }
    }
}
