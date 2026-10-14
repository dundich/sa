namespace Sa.Data.TempFolder.Cleanup;

/// <summary>
/// Decides which subfolders under the instance root a cleanup pass should delete — top-level,
/// nested, or both, according to the strategy. Selected per registration — either the built-in
/// strategy named by <see cref="TempFolderOptions.Cleanup"/> or a code override registered with
/// <c>UseCleanupStrategy&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// Strategies only <b>select</b>; the instance performs the actual deletion, so the path guard
/// (root containment), the <see cref="TempFolderOptions.MaxFoldersPerPass"/> budget and the
/// retry policy apply to every strategy alike. Paths returned outside the root are skipped and
/// logged instead of deleted.
/// </remarks>
public interface ICleanupStrategy
{
    /// <summary>
    /// Returns the absolute paths of the subfolders to delete, oldest first, already capped at
    /// <see cref="CleanupContext.Options"/>'s <c>MaxFoldersPerPass</c>.
    /// </summary>
    /// <param name="context">Root, normalised options and the time provider of the calling instance.</param>
    /// <returns>
    /// Absolute paths of subfolders under the root. May be empty. The instance re-validates every
    /// entry against the root before deleting.
    /// </returns>
    IReadOnlyList<string> SelectForDeletion(CleanupContext context);
}
