using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Sa.Schedule;

public interface IScheduleBuilder
{
    /// <summary>
    /// Adds a job of type <typeparamref name="T"/> to the schedule.
    /// </summary>
    /// <typeparam name="T">The type of job to add.</typeparam>
    /// <param name="jobId">The ID of the job. If not specified, a new ID will be generated.</param>
    /// <returns>A builder for the added job.</returns>
    IJobBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>
        (Guid? jobId = null) where T : class, IJob;

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
    IScheduleBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>
        (Action<IServiceProvider, IJobBuilder> configure, Guid? jobId = null) where T : class, IJob;

    /// <summary>
    /// Adds an interceptor of type <typeparamref name="T"/> to the schedule.
    /// </summary>
    /// <typeparam name="T">The type of interceptor to add.</typeparam>
    /// <param name="key">The key to use for the interceptor. If not specified, a default key will be used.</param>
    /// <returns>The schedule builder.</returns>
    IScheduleBuilder AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>
        (object? key = null) where T : class, IJobInterceptor;

    /// <summary>
    /// Configures the schedule to use a hosted service.
    /// </summary>
    /// <remarks>
    /// Idempotent: calling it from several builders still registers a single hosted service.
    /// </remarks>
    /// <returns>The schedule builder.</returns>
    IScheduleBuilder UseHostedService();

    /// <summary>
    /// Adds an error handler to the schedule.
    /// </summary>
    /// <param name="handler">
    /// A function to handle errors that occur during job execution. Returning <c>true</c>
    /// consumes the error; returning <c>false</c> leaves it to the per-job error policy.
    /// </param>
    /// <remarks>
    /// Handlers accumulate across calls, including across separate
    /// <see cref="Setup.AddSaSchedule"/> calls — the error is considered handled
    /// as soon as any handler returns <c>true</c>. A <c>null</c> handler throws
    /// <see cref="ArgumentNullException"/>.
    /// </remarks>
    /// <returns>The schedule builder.</returns>
    IScheduleBuilder AddErrorHandler(Func<IJobContext, Exception, bool> handler);

    // ---------- settings: the standard options pipeline ----------

    /// <summary>
    /// Binds <see cref="ScheduleOptions"/> from the given configuration section, e.g.
    /// <c>"Schedule"</c> — the section otherwise passed as <c>AddSaSchedule</c>'s
    /// <c>configSectionPath</c> argument, moved into the one registration delegate.
    /// </summary>
    /// <remarks>
    /// Recorded on the builder; <c>AddSaSchedule</c> binds it in the fixed slot before the
    /// <see cref="Options"/> actions — so configuration wins over code, and an
    /// <see cref="Options"/> <c>Configure</c> always wins over configuration, no matter
    /// where this call sits in the callback. May be called several times; the last path
    /// wins. A call whose builder uses this method (or <see cref="Options"/>) is a
    /// *configuring* call: it counts toward the one-configuring-call-per-collection
    /// guard, while job-only calls stay repeatable.
    /// </remarks>
    /// <param name="configSectionPath">Configuration section path, e.g. <c>"Schedule"</c>.</param>
    IScheduleBuilder FromConfiguration(string configSectionPath);

    /// <summary>
    /// Hands the standard <see cref="OptionsBuilder{TOptions}"/> for
    /// <see cref="ScheduleOptions"/> to the callback — the <c>Configure</c> /
    /// <c>PostConfigure</c> / <c>Validate</c> surface of the options pipeline, in one
    /// registration channel next to the jobs.
    /// </summary>
    /// <remarks>
    /// Runs in the options pipeline <b>after</b> the <c>BindConfiguration</c> call made
    /// for <see cref="FromConfiguration"/>, so a <c>Configure</c> here is the settings-level
    /// escape hatch that beats configuration, while a <c>Validate</c> adds to — rather
    /// than replaces — the built-in checks. May be called several times; the actions run
    /// in call order.
    /// <para>
    /// A <see cref="Setup.AddSaSchedule"/> call whose builder uses this method is a
    /// *configuring* call: it counts toward the one-configuring-call-per-collection
    /// guard, while job-only calls stay repeatable (that is how libraries contribute
    /// jobs to a shared schedule).
    /// </para>
    /// </remarks>
    /// <param name="configureSettings">Callback receiving the settings options builder.</param>
    IScheduleBuilder Options(Action<OptionsBuilder<ScheduleOptions>> configureSettings);
}
