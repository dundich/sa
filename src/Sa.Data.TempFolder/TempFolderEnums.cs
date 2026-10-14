namespace Sa.Data.TempFolder;

/// <summary>
/// The timestamp a cleanup strategy reads to decide whether a subfolder is expired.
/// </summary>
public enum TempFolderAgeSource
{
    /// <summary>
    /// Age from the folder's last write time. Active folders can refresh this marker themselves —
    /// see <see cref="TempFolderOptions.TouchDebounce"/>. Default.
    /// </summary>
    LastWriteTime = 0,

    /// <summary>
    /// Age from the folder's creation time. The marker never moves, so a long-lived but active
    /// folder will still be treated as expired once <see cref="TempFolderOptions.MaxAge"/> passes.
    /// </summary>
    CreationTime = 1,
}

/// <summary>
/// Which cleanup strategy the instance uses when no code override
/// (<c>UseCleanupStrategy&lt;T&gt;()</c>) was registered for it.
/// </summary>
public enum TempFolderCleanupKind
{
    /// <summary>
    /// Deletes subfolders older than <see cref="TempFolderOptions.MaxAge"/>, filtered by
    /// <see cref="TempFolderOptions.FolderPrefix"/> at the top level: an expired top-level folder
    /// goes wholesale together with everything inside it, while a still-fresh top-level folder is
    /// descended into and judged by the same rule at every inner level — so date hierarchies like
    /// <c>yyyy/MM/dd/HH</c> shed their old day/hour folders while the year/month containers stay.
    /// Default.
    /// </summary>
    AgeBased = 0,
}

/// <summary>
/// Which folder-naming strategy the instance uses when no code override
/// (<c>UseNamingStrategy&lt;T&gt;()</c>) was registered for it.
/// </summary>
public enum TempFolderNamingKind
{
    /// <summary>
    /// <c>{FolderPrefix}{Guid-v7:N}</c> — time-sortable, collision-free names. Default.
    /// </summary>
    GuidV7 = 0,

    /// <summary>
    /// <c>{FolderPrefix}{current local time shaped by <see cref="TempFolderOptions.FolderNameFormat"/>}</c> —
    /// e.g. <c>up_2026-10-08</c> or, with the format <c>yyyy/MM/dd/HH</c>, <c>up_2026/10/08/14</c>.
    /// Calls inside one format bucket share the same folder; the age-based cleanup then ages the
    /// hierarchy level by level.
    /// </summary>
    Date = 1,
}
