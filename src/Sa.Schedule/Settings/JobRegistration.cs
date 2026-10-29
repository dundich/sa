using System.Diagnostics.CodeAnalysis;

namespace Sa.Schedule.Settings;

/// <summary>
/// Records one job registration (id + type) in the service collection.
/// <para>
/// The job settings themselves may be registered through a factory (the
/// <c>AddJob(Action&lt;IServiceProvider, IJobBuilder&gt;, ...)</c> overload), so the id/type
/// pair is recorded separately: it lets <see cref="ScheduleBuilder"/> reject a duplicate
/// explicit id at registration time instead of at host start, when
/// <see cref="ScheduleSettings"/> would otherwise be the first place to notice.
/// </para>
/// </summary>
internal sealed record JobRegistration(Guid JobId, Type JobType);

/// <summary>
/// The one message for "two different job types share an explicit id", shared by the
/// registration-time check and the settings-time safety net.
/// </summary>
internal static class JobIdGuard
{
    [DoesNotReturn]
    public static void Throw(Guid jobId, Type existingType, Type incomingType, string paramName)
        => throw new ArgumentException(
            $"Two different job types share the explicit job id '{jobId}': " +
            $"'{existingType.FullName}' and '{incomingType.FullName}'. " +
            "Job ids must be unique per schedule — pass distinct ids, or omit " +
            "jobId to have one generated per registration.",
            paramName);
}
