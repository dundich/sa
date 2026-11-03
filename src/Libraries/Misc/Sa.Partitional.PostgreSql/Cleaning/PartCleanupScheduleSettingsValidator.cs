using Microsoft.Extensions.Options;

namespace Sa.Partitional.PostgreSql.Cleaning;

/// <summary>
/// Validates <see cref="PartCleanupScheduleSettings"/> for the options pipeline. Registered once by
/// type, so it runs for every named instance (the default module and every keyed module).
/// </summary>
/// <remarks>
/// Deliberately <c>IValidateOptions</c> rather than <c>ValidateDataAnnotations()</c>: the latter is
/// marked <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT. Registered alongside
/// <c>ValidateOnStart()</c>, so an invalid section surfaces as an
/// <see cref="OptionsValidationException"/> at host start — and the same check also runs when the
/// options are first read.
/// </remarks>
internal sealed class PartCleanupScheduleSettingsValidator : IValidateOptions<PartCleanupScheduleSettings>
{
    public ValidateOptionsResult Validate(string? name, PartCleanupScheduleSettings options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Skip;
        }

        try
        {
            options.Validate();
        }
        catch (System.ComponentModel.DataAnnotations.ValidationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }

        return ValidateOptionsResult.Success;
    }
}
