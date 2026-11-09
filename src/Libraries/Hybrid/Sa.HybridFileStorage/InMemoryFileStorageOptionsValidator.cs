using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage;

/// <summary>
/// Validates <see cref="InMemoryFileStorageOptions"/> through the standard pipeline, so a
/// malformed basket fails as an <see cref="OptionsValidationException"/> at the first read (or at
/// host start via <c>ValidateOnStart()</c>) instead of producing a file ID no other provider can
/// parse. Registered by <see cref="HybridStorageRegistrar"/>, together with the explicit-instance
/// channels' own eager <see cref="InMemoryFileStorageOptions.Validate"/> call.
/// </summary>
internal sealed class InMemoryFileStorageOptionsValidator : IValidateOptions<InMemoryFileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, InMemoryFileStorageOptions options)
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
