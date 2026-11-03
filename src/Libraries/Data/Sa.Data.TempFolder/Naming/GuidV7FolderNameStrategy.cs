namespace Sa.Data.TempFolder.Naming;

/// <summary>
/// The default naming strategy: <c>{FolderPrefix}{Guid-v7:N}</c>.
/// </summary>
/// <remarks>
/// GUID v7 embeds its timestamp in the high bits, so generated names sort chronologically —
/// which makes a plain <c>ls</c> of the temp root read as a creation-order listing, and keeps
/// name generation collision-free without coordination.
/// </remarks>
public sealed class GuidV7FolderNameStrategy : IFolderNameStrategy
{
    /// <inheritdoc />
    public string CreateFolderName(TempFolderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return $"{options.FolderPrefix}{Guid.CreateVersion7():N}";
    }
}
