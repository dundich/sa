namespace Sa.Outbox.Delivery;

/// <summary>
/// Result of <see cref="ConsumeSettings.Validate"/>.
/// </summary>
public sealed class ConsumeSettingsValidationResult
{
    internal static readonly ConsumeSettingsValidationResult Valid = new([]);

    private ConsumeSettingsValidationResult(List<string> errors)
    {
        Errors = errors;
        IsValid = errors.Count == 0;
    }

    /// <summary>
    /// True if all settings are valid.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// List of validation error messages. Empty when <see cref="IsValid"/> is true.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    internal static ConsumeSettingsValidationResult Fail(List<string> errors)
        => new(errors);
}
