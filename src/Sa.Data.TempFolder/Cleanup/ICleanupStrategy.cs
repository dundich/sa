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
/// logged instead of deleted; a list longer than the budget is truncated by the instance.
/// </remarks>
public interface ICleanupStrategy
{
    /// <summary>
    /// Returns the absolute paths of the subfolders to delete, <b>oldest first</b>. The built-in
    /// strategy already caps the list at <c>MaxFoldersPerPass</c>; a custom one may return more,
    /// but the instance keeps only the first <c>MaxFoldersPerPass</c> entries — so ordering the
    /// result oldest-first is what leaves the freshest of the expired folders for the next pass.
    /// </summary>
    /// <param name="context">Root, normalised options and the time provider of the calling instance.</param>
    /// <returns>
    /// Absolute paths of subfolders under the root. May be empty. The instance re-validates every
    /// entry against the root before deleting.
    /// </returns>
    IReadOnlyList<string> SelectForDeletion(CleanupContext context);
}
