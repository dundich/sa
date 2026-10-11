using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Validates the PostgreSQL storage options through the standard pipeline, so an invalid
/// table name, storage type, basket or schedule fails as an <see cref="OptionsValidationException"/>
/// at first read (or at host start via <c>ValidateOnStart()</c>) instead of reaching the DDL
/// and surfacing as <c>relation does not exist</c> on the first upload.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PostgresFileStorageOptions.Validate"/> reports the first offender as an
/// <see cref="ArgumentException"/> — the validator translates that into a pipeline failure,
/// so the message (which names the offending property) rides along.
/// </para>
/// <para>
/// The same instance also enforces cross-registration table uniqueness, because a single
/// <see cref="IValidateOptions{T}"/> serves every named instance: it claims the table in
/// <see cref="PostgresFileStorageTableRegistry"/> for the registration key (the options-instance
/// name), so two keyed storages cannot own the same physical table.
/// </para>
/// </remarks>
internal sealed class PostgresFileStorageOptionsValidator(PostgresFileStorageTableRegistry tables)
    : IValidateOptions<PostgresFileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, PostgresFileStorageOptions options)
    {
        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }

        return tables.Claim(name, options.TableName) is { } collision
            ? ValidateOptionsResult.Fail(collision)
            : ValidateOptionsResult.Success;
    }
}
