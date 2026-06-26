namespace Sa.Schedule;

public interface IJobErrorHandlingBuilder
{
    /// <summary>
    /// Specifies the retry policy for the job in case of an error.
    /// </summary>
    /// <param name="count">The number of times to retry the job. If null, the job will not be retried.</param>
    /// <returns>The current IJobErrorHandlingBuilder instance.</returns>
    IJobErrorHandlingBuilder IfErrorRetry(int? count = null);

    /// <summary>
    /// Specifies that the application should be closed if an error occurs.
    /// </summary>
    /// <returns>The current IJobErrorHandlingBuilder instance.</returns>
    IJobErrorHandlingBuilder ThenCloseApplication();

    /// <summary>
    /// Specifies that the current job should be aborted (stopped) if an error occurs.
    /// </summary>
    /// <returns>The current IJobErrorHandlingBuilder instance.</returns>
    IJobErrorHandlingBuilder ThenAbortJob();

    /// <summary>
    /// Specifies that all jobs should be stopped if an error occurs.
    /// </summary>
    /// <returns>The current IJobErrorHandlingBuilder instance.</returns>
    IJobErrorHandlingBuilder ThenStopAllJobs();

    /// <summary>
    /// Specifies that the current job should be stopped if an error occurs.
    /// </summary>
    /// <returns>The current IJobErrorHandlingBuilder instance.</returns>
    [Obsolete("Use ThenAbortJob instead. This method will be removed in a future version.")]
    IJobErrorHandlingBuilder ThenStopJob() => ThenAbortJob();

    /// <summary>
    /// Specifies a custom error suppression policy.
    /// </summary>
    /// <param name="suppressError">A function that determines whether an error should be suppressed.</param>
    /// <returns>The current IJobErrorHandlingBuilder instance.</returns>
    IJobErrorHandlingBuilder DoSuppressError(Func<Exception, bool>? suppressError = null);
}
