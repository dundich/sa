namespace Sa.Data.TempFolder;

/// <summary>
/// Payload handed to <see cref="TempFolderOptions.OnFreeSpaceReached"/> when the free space of the
/// volume holding a temp-folder root crosses <see cref="TempFolderOptions.MinFreeSpace"/>.
/// </summary>
/// <param name="Name">The registration key (keyed-service name) of the temp folder instance.</param>
/// <param name="RootPath">The root directory whose volume was probed.</param>
/// <param name="AvailableFreeSpace">Free space in bytes on that volume at the moment of the transition.</param>
/// <param name="MinFreeSpace">The configured limit in bytes.</param>
/// <param name="Reached">
/// <see langword="true"/> on the transition into "the limit is reached" (available space dropped
/// to or below <see cref="TempFolderOptions.MinFreeSpace"/>),
/// <see langword="false"/> on the transition back into "above the limit". The event is
/// edge-triggered: it never repeats while the state does not change.
/// </param>
public sealed class TempFolderFreeSpaceArgs(
    string name,
    string rootPath,
    long availableFreeSpace,
    long minFreeSpace,
    bool reached)
{
    /// <summary>The registration key (keyed-service name) of the temp folder instance.</summary>
    public string Name { get; } = name;

    /// <summary>The root directory whose volume was probed.</summary>
    public string RootPath { get; } = rootPath;

    /// <summary>Free space in bytes on that volume at the moment of the transition.</summary>
    public long AvailableFreeSpace { get; } = availableFreeSpace;

    /// <summary>The configured limit in bytes.</summary>
    public long MinFreeSpace { get; } = minFreeSpace;

    /// <summary>
    /// <see langword="true"/> on the transition into "the limit is reached",
    /// <see langword="false"/> on the transition back into "above the limit".
    /// </summary>
    public bool Reached { get; } = reached;
}
