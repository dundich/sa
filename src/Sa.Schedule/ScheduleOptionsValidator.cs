using Microsoft.Extensions.Options;
using Sa.Schedule.Cron;

namespace Sa.Schedule;

/// <summary>
/// Валидирует <see cref="ScheduleOptions"/> для конвейера options.
/// </summary>
/// <remarks>
/// Проверяет *значения* секций, присутствующих в конфигурации, независимо от того,
/// соответствует ли ключ какой-то зарегистрированной задаче: проверка синтаксиса не зависит
/// от контейнера. Секции, которым задача не найдена, валидация не мешает — их просто
/// никто не применит.
/// <para>
/// Намеренно <c>IValidateOptions</c>, а не <c>ValidateDataAnnotations()</c>: последний
/// помечен <c>RequiresUnreferencedCode</c> (IL2026) и ломает Native AOT, ради которого
/// эта сборка и существует. Валидация срабатывает и при чтении
/// <c>IOptions&lt;T&gt;.Value</c> (не только при <c>ValidateOnStart()</c>), поэтому
/// ошибка всплывает как <see cref="OptionsValidationException"/> при первой сборке
/// настроек планировщика.
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
                    _ = new CronTiming(job.Cron);
                }
                catch (Exception ex)
                {
                    return ValidateOptionsResult.Fail(
                        $"ScheduleOptions:Jobs:{jobName}: 'Cron' value '{job.Cron}' is not a valid " +
                        $"5-field cron expression: {ex.Message}");
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
        }

        return ValidateOptionsResult.Success;
    }
}
