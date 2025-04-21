namespace Sa.Schedule;

/// <summary>
/// Defines an interface for job interceptors, allowing for custom logic to be executed before or after a job is handled.
/// </summary>
public interface IJobInterceptor
{
    /// <summary>
    /// Called when a job is being handled, allowing the interceptor to perform custom logic before or after the job is executed.
    /// </summary>
    /// <param name="context">The context of the job being handled.</param>
    /// <param name="next">A function that represents the next step in the job handling pipeline.</param>
    /// <param name="key">An optional key associated with the job.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the job handling operation.</param>
    /// <returns>A task that represents the result of the job handling operation.</returns>
    Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken cancellationToken);
}