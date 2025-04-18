using System.Diagnostics.CodeAnalysis;

namespace Sa.Schedule;

public interface IScheduleBuilder
{
    /// <summary>
    /// Adds a job of type <typeparamref name="T"/> to the schedule.
    /// </summary>
    /// <typeparam name="T">The type of job to add.</typeparam>
    /// <param name="jobId">The ID of the job. If not specified, a new ID will be generated.</param>
    /// <returns>A builder for the added job.</returns>
    IJobBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Guid? jobId = null) where T : class, IJob;

    /// <summary>
    /// Adds a job with the specified action to the schedule.
    /// </summary>
    /// <param name="action">The action to execute when the job is run.</param>
    /// <param name="jobId">The ID of the job. If not specified, a new ID will be generated.</param>
    /// <returns>A builder for the added job.</returns>
    IJobBuilder AddJob(Func<IJobContext, CancellationToken, Task> action, Guid? jobId = null);

    /// <summary>
    /// Adds a job of type <typeparamref name="T"/> to the schedule and configures it using the specified action.
    /// </summary>
    /// <typeparam name="T">The type of job to add.</typeparam>
    /// <param name="configure">An action to configure the job.</param>
    /// <param name="jobId">The ID of the job. If not specified, a new ID will be generated.</param>
    /// <returns>The schedule builder.</returns>
    IScheduleBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Action<IServiceProvider, IJobBuilder> configure, Guid? jobId = null) where T : class, IJob;

    /// <summary>
    /// Adds an interceptor of type <typeparamref name="T"/> to the schedule.
    /// </summary>
    /// <typeparam name="T">The type of interceptor to add.</typeparam>
    /// <param name="key">The key to use for the interceptor. If not specified, a default key will be used.</param>
    /// <returns>The schedule builder.</returns>
    IScheduleBuilder AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(object? key = null) where T : class, IJobInterceptor;

    /// <summary>
    /// Configures the schedule to use a hosted service.
    /// </summary>
    /// <returns>The schedule builder.</returns>
    IScheduleBuilder UseHostedService();

    /// <summary>
    /// Adds an error handler to the schedule.
    /// </summary>
    /// <param name="handler">A function to handle errors that occur during job execution.</param>
    /// <returns>The schedule builder.</returns>
    IScheduleBuilder AddErrorHandler(Func<IJobContext, Exception, bool> handler);
}
