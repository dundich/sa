using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Validates the PostgreSQL storage options through the standard pipeline, so an invalid
/// table name, storage type, basket or schedule fails as an <see cref="OptionsValidationException"/>
/// at first read (or at host start via <c>ValidateOnStart()</c>) instead of reaching the DDL
/// and surfacing as <c>relation does not exist</c> on the first upload.
/// </summary>
/// <remarks>
/// <see cref="PostgresFileStorageOptions.Validate"/> reports the first offender as an
/// <see cref="ArgumentException"/> — the validator translates that into a pipeline failure,
/// so the message (which names the offending property) rides along.
/// </remarks>
internal sealed class PostgresFileStorageOptionsValidator : IValidateOptions<PostgresFileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, PostgresFileStorageOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
