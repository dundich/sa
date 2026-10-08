namespace Sa.Data.TempFolder.Cleanup;

/// <summary>
/// Everything an <see cref="ICleanupStrategy"/> may read about the instance running the pass.
/// </summary>
public sealed class CleanupContext
{
    /// <summary>The instance root; every returned path must live under it.</summary>
    public required string RootPath { get; init; }

    /// <summary>The instance's normalised options snapshot.</summary>
    public required TempFolderOptions Options { get; init; }

    /// <summary>
    /// The clock the instance ages against — injectable so tests do not sleep through
    /// <see cref="TempFolderOptions.MaxAge"/>.
    /// </summary>
    public required TimeProvider TimeProvider { get; init; }
}
