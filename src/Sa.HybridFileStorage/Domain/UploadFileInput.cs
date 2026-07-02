namespace Sa.HybridFileStorage.Domain;

/// <summary>
/// Represents input metadata for a file upload operation.
/// </summary>
public sealed record UploadFileInput
{
    /// <summary>
    /// Gets or sets the tenant identifier associated with the file. Defaults to 0.
    /// </summary>
    public int TenantId { get; init; } = 0;

    /// <summary>
    /// Gets or sets the file name. Defaults to an empty string.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Gets a default (empty) <see cref="UploadFileInput"/> instance.
    /// </summary>
    public static UploadFileInput Empty { get; } = new();

    /// <summary>
    /// Validates the input metadata. Throws <see cref="ArgumentException"/> if validation fails.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FileName))
            throw new ArgumentException("File name cannot be null or empty.", nameof(FileName));

        if (TenantId < 0)
            throw new ArgumentException("TenantId must be greater than or equal to 0.", nameof(TenantId));
    }
}
