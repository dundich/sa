namespace Sa.Data.TempFolder;

/// <summary>
/// Payload handed to <see cref="TempFolderOptions.OnVolumeExceeded"/> when the measured volume of a
/// temp folder crosses <see cref="TempFolderOptions.MaxTotalSize"/>.
/// </summary>
/// <param name="Name">The registration key (keyed-service name) of the temp folder instance.</param>
/// <param name="RootPath">The root directory the measurement covers.</param>
/// <param name="TotalSize">Measured total size in bytes at the moment of the transition.</param>
/// <param name="MaxTotalSize">The configured limit in bytes.</param>
/// <param name="Exceeded">
/// <see langword="true"/> on the transition into "over the limit",
/// <see langword="false"/> on the transition back into "within the limit". The event is
/// edge-triggered: it never repeats while the state does not change.
/// </param>
public sealed class TempFolderVolumeArgs(
    string name,
    string rootPath,
    long totalSize,
    long maxTotalSize,
    bool exceeded)
{
    /// <summary>The registration key (keyed-service name) of the temp folder instance.</summary>
    public string Name { get; } = name;

    /// <summary>The root directory the measurement covers.</summary>
    public string RootPath { get; } = rootPath;

    /// <summary>Measured total size in bytes at the moment of the transition.</summary>
    public long TotalSize { get; } = totalSize;

    /// <summary>The configured limit in bytes.</summary>
    public long MaxTotalSize { get; } = maxTotalSize;

    /// <summary>
    /// <see langword="true"/> on the transition into "over the limit",
    /// <see langword="false"/> on the transition back into "within the limit".
    /// </summary>
    public bool Exceeded { get; } = exceeded;
}
