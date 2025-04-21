namespace Sa.Schedule;

public class JobException(IJobContext context, Exception? innerException)
    : Exception($"[{context.JobName}] job error", innerException)
{
    public IJobContext JobContext { get; } = context.Clone();
}
