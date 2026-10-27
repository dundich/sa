using Microsoft.Extensions.Options;
using Sa.Schedule.Cron;

namespace Sa.Schedule;

/// <summary>
/// Validates <see cref="ScheduleOptions"/> for the options pipeline.
/// </summary>
/// <remarks>
/// Checks the <i>values</i> of every section present in configuration, regardless of whether
/// its key matches a registered job: syntax validation does not depend on the container.
/// Sections with no matching job are not an error — they are simply never applied.
/// <para>
/// Cron values are checked with <see cref="CronTiming.Strict"/>: besides the syntax, an
/// expression that can never fire (31 April) is rejected here instead of turning into a job
/// loop that aborts on its first <c>GetNextOccurrence</c> returning null.
/// </para>
/// <para>
/// Deliberately <c>IValidateOptions</c> rather than <c>ValidateDataAnnotations()</c>: the
/// latter is marked <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT, which is
/// why this assembly exists. Validation also runs when <c>IOptions&lt;T&gt;.Value</c> is read
/// (not only under <c>ValidateOnStart()</c>), so an invalid value surfaces as an
/// <see cref="OptionsValidationException"/> when the scheduler settings are first built.
/// </para>
/// </remarks>
internal sealed class ScheduleOptionsValidator : IValidateOptions<ScheduleOptions>
{
    public ValidateOptionsResult Validate(string? name, ScheduleOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Skip;
        }

        foreach (var (jobName, job) in options.Jobs)
        {
            if (job is null)
            {
                continue;
            }

            if (job.Cron is not null && job.Every is not null)
            {
                return ValidateOptionsResult.Fail(
                    $"ScheduleOptions:Jobs:{jobName}: 'Cron' and 'Every' are mutually exclusive — " +
                    "specify at most one.");
            }

            if (job.Cron is not null)
            {
                try
                {
                    // Strict, not the lenient ctor: an expression that can never fire
                    // (31 April, 30 February) would otherwise pass validation and then
                    // abort the job loop silently on the first GetNextOccurrence == null.
                    _ = CronTiming.Strict(job.Cron);
                }
                catch (Exception ex)
                {
                    return ValidateOptionsResult.Fail(
                        $"ScheduleOptions:Jobs:{jobName}: 'Cron' value '{job.Cron}' is not a valid " +
                        $"5- or 6-field cron expression: {ex.Message}");
                }
            }

            if (job.Every is not null && job.Every.Value <= TimeSpan.Zero)
            {
                return ValidateOptionsResult.Fail(
                    $"ScheduleOptions:Jobs:{jobName}: 'Every' must be greater than zero, " +
                    $"but was '{job.Every.Value}'.");
            }

            if (job.InitialDelay is not null && job.InitialDelay.Value < TimeSpan.Zero)
            {
                return ValidateOptionsResult.Fail(
                    $"ScheduleOptions:Jobs:{jobName}: 'InitialDelay' must not be negative, " +
                    $"but was '{job.InitialDelay.Value}'.");
            }

            // The same rules the code mutators enforce (WithConcurrencyLimit throws for
            // < 0, WithMaxConcurrency throws for < 1), surfaced here as an
            // OptionsValidationException instead.
            if (job.ConcurrencyLimit is not null && job.ConcurrencyLimit.Value < 0)
            {
                return ValidateOptionsResult.Fail(
                    $"ScheduleOptions:Jobs:{jobName}: 'ConcurrencyLimit' must not be negative, " +
                    $"but was '{job.ConcurrencyLimit.Value}'.");
            }

            if (job.MaxConcurrency is not null && job.MaxConcurrency.Value < 1)
            {
                return ValidateOptionsResult.Fail(
                    $"ScheduleOptions:Jobs:{jobName}: 'MaxConcurrency' must be at least 1, " +
                    $"but was '{job.MaxConcurrency.Value}'.");
            }

            if (job.TimeZone is not null && !TryFindTimeZone(job.TimeZone))
            {
                return ValidateOptionsResult.Fail(
                    $"ScheduleOptions:Jobs:{jobName}: 'TimeZone' value '{job.TimeZone}' is not a " +
                    "known time zone id (IANA like 'Europe/Moscow', or a Windows id like " +
                    "'Russian Standard Time').");
            }
        }

        if (options.TimeZone is not null && !TryFindTimeZone(options.TimeZone))
        {
            return ValidateOptionsResult.Fail(
                $"ScheduleOptions: 'TimeZone' value '{options.TimeZone}' is not a " +
                "known time zone id (IANA like 'Europe/Moscow', or a Windows id like " +
                "'Russian Standard Time').");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool TryFindTimeZone(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
