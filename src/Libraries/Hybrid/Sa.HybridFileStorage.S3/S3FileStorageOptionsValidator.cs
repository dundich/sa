using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage.S3;

/// <summary>
/// Validates <see cref="S3FileStorageOptions"/> for the options pipeline.
/// </summary>
/// <remarks>
/// Registered by <see cref="Setup.AddSaS3FileStorage"/> together with <c>ValidateOnStart()</c>, so an
/// invalid configuration surfaces as an <see cref="OptionsValidationException"/> when the host starts
/// rather than as a <see cref="System.ComponentModel.DataAnnotations.ValidationException"/> from the
/// middle of an upload. The underlying <see cref="S3FileStorageOptions.Validate"/> keeps its own
/// exception type so the same checks stay usable from a non-DI construction path.
/// </remarks>
internal sealed class S3FileStorageOptionsValidator : IValidateOptions<S3FileStorageOptions>
{
    /// <summary>
    /// Validates the options instance with the given name.
    /// </summary>
    /// <param name="name">The name of the options instance being validated.</param>
    /// <param name="options">The options instance to validate.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Skip"/> when <paramref name="options"/> is <c>null</c>,
    /// <see cref="ValidateOptionsResult.Success"/> when it is valid, otherwise a failure carrying
    /// the validation message.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, S3FileStorageOptions options)
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
