using Microsoft.Extensions.Options;

namespace Sa.Utils.WorkQueue;

/// <summary>
/// Validates <see cref="SaWorkQueueSettings"/> for the options pipeline.
/// </summary>
/// <remarks>
/// The same rules the <see cref="SaWorkQueue{TInput}"/> constructor enforces, checked
/// earlier — as an <see cref="OptionsValidationException"/> at host start
/// (<c>ValidateOnStart()</c>) or on the first read of the options — so a typo in
/// <c>appsettings</c> surfaces as a configuration error rather than an
/// <see cref="ArgumentOutOfRangeException"/> from inside the queue.
/// <para>
/// Deliberately <c>IValidateOptions</c>, not <c>ValidateDataAnnotations()</c>: the latter
/// is marked <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT, which is the
/// reason this assembly exists. Registered by type via <c>TryAddEnumerable</c>, so a
/// repeated bare call deduplicates instead of stacking.
/// </para>
/// </remarks>
internal sealed class SaWorkQueueSettingsValidator : IValidateOptions<SaWorkQueueSettings>
{
    public ValidateOptionsResult Validate(string? name, SaWorkQueueSettings options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Skip;
        }

        List<string>? errors = null;

        if (options.QueueCapacity is < 1)
        {
            Fail(ref errors, "SaWorkQueueSettings:QueueCapacity must be at least 1.");
        }

        if (options.ConcurrencyLimit is < 0)
        {
            Fail(ref errors, "SaWorkQueueSettings:ConcurrencyLimit must be 0 (paused) or positive.");
        }

        if (options.MaxConcurrency is < 0)
        {
            Fail(ref errors, "SaWorkQueueSettings:MaxConcurrency must be 0 (processor count) or positive.");
        }

        if (options.ShutdownTimeout is { } shutdownTimeout && shutdownTimeout <= TimeSpan.Zero)
        {
            Fail(ref errors, "SaWorkQueueSettings:ShutdownTimeout must be positive.");
        }

        return errors is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);

        static void Fail(ref List<string>? errors, string message)
            => (errors ??= []).Add(message);
    }
}
