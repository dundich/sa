namespace Sa.Data.TempFolder;

/// <summary>
/// One temp-folder instance: a root directory plus the strategies and policies registered for it.
/// Instances are keyed services — <c>sp.GetRequiredKeyedService&lt;ITempFolder&gt;(name)</c> — so
/// one host can run several roots with different prefixes, ages, strategies and limits.
/// </summary>
/// <remarks>
/// Every path a caller passes is resolved through <see cref="PathGuard"/>: it may be relative to
/// <see cref="RootPath"/> or absolute — either way the resolved result must be the root itself or
/// live under it, otherwise a <see cref="System.Security.SecurityException"/> is thrown. Hostile
/// input is rejected even when it would have stayed inside the root: <c>..</c> segments (on either
/// separator), <c>~</c> (never expanded), shell/glob metacharacters (<c>&lt; &gt; | &amp; ; ` $ ^ * ? " ' %</c>),
/// control and invisible Unicode characters (bidi overrides, zero-width), and — on Windows — a
/// colon outside the drive spec. A read-only instance
/// (<see cref="TempFolderOptions.ReadOnly"/>) rejects every mutating call with
/// <see cref="InvalidOperationException"/> and is ignored by the background service entirely.
/// </remarks>
public interface ITempFolder : IDisposable
{
    /// <summary>The registration key this instance was registered under.</summary>
    string Name { get; }

    /// <summary>The fully qualified root directory of this instance.</summary>
    string RootPath { get; }

    /// <summary>
    /// Creates a subfolder under the root and returns its absolute path.
    /// </summary>
    /// <param name="relativeSubfolder">
    /// An optional caller-chosen subfolder — relative to the root or absolute (when absolute it
    /// must still land inside the root) — possibly nested, e.g. <c>"job/42"</c>; parents are
    /// created as needed. When <see langword="null"/> or blank, the name comes from the
    /// instance's naming strategy — by default <c>{FolderPrefix}{Guid-v7:N}</c>.
    /// </param>
    /// <returns>The absolute path of the (new or existing) subfolder.</returns>
    /// <exception cref="InvalidOperationException">The instance is read-only.</exception>
    /// <exception cref="System.Security.SecurityException">
    /// The path escapes the root, or contains an injection-style construct (see the remarks above).
    /// </exception>
    string CreateSubfolder(string? relativeSubfolder = null);

    /// <summary>
    /// Writes <paramref name="source"/> into a file inside the temp folder, creating parent
    /// directories as needed, and (debounced) activates the touched folder chain — the file's own
    /// directory and every ancestor up to the root. The whole write is one activity against the
    /// cleanup pass: a concurrent pass is cancelled rather than allowed to race it (see
    /// <see cref="CleanupAsync"/>).
    /// </summary>
    /// <param name="source">The stream to copy from; its current position is respected.</param>
    /// <param name="relativePath">
    /// Target file path, including the file name: relative to the root or absolute — an absolute
    /// path is accepted when it resolves inside the root (see the remarks above).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The canonical relative path (relative to the root) and the absolute path of the written file.
    /// </returns>
    /// <exception cref="InvalidOperationException">The instance is read-only, or the path points at a directory.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is empty or whitespace.</exception>
    /// <exception cref="IOException">
    /// A file already exists at the target path and <see cref="TempFolderOptions.OverwriteFiles"/>
    /// is <see langword="false"/> — the original is left intact.
    /// </exception>
    /// <exception cref="System.Security.SecurityException">
    /// The path resolves outside the root, or contains an injection-style construct (see the remarks above).
    /// </exception>
    ValueTask<(string RelativePath, string AbsolutePath)> SaveStreamAsync(
        Stream source, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies an external file into the temp folder. Shares the same activity exclusion as
    /// <see cref="SaveStreamAsync"/>: the copy never overlaps a cleanup pass.
    /// </summary>
    /// <param name="sourcePath">Path of the file to copy from; must exist. Its file name must be
    /// free of injection-style characters — the name is reused inside the temp folder.</param>
    /// <param name="relativeSubfolder">
    /// Target subfolder under the root — relative or absolute (when absolute it must still land
    /// inside the root). When <see langword="null"/> or blank, a fresh strategy-named subfolder
    /// is created and the file keeps its own name inside it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The canonical relative path (relative to the root) and the absolute path of the copy.
    /// </returns>
    /// <exception cref="InvalidOperationException">The instance is read-only.</exception>
    /// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> does not exist.</exception>
    /// <exception cref="IOException">
    /// A file already exists at the destination and <see cref="TempFolderOptions.OverwriteFiles"/>
    /// is <see langword="false"/> — the original is left intact.
    /// </exception>
    /// <exception cref="System.Security.SecurityException">
    /// The target path escapes the root or contains an injection-style construct (see the remarks
    /// above), or the source file's own name contains such a construct.
    /// </exception>
    ValueTask<(string RelativePath, string AbsolutePath)> CopyFileAsync(
        string sourcePath, string? relativeSubfolder = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously enumerates files matching <paramref name="pattern"/> and returns their
    /// absolute paths.
    /// </summary>
    /// <param name="pattern">File pattern, e.g. <c>"*.dat"</c> or <c>"report_?.csv"</c> — the
    /// pattern is a pattern, not a path, so <c>*</c>/<c>?</c> are allowed there.</param>
    /// <param name="relativeSubfolder">Subfolder to search — relative or absolute (when absolute
    /// it must still land inside the root); <see langword="null"/> or blank searches the root.</param>
    /// <param name="recursive">Whether to descend into nested subfolders. Defaults to <see langword="false"/>.</param>
    /// <param name="cancellationToken">Cancellation token; also surfaces as the iterator's token.</param>
    /// <exception cref="System.Security.SecurityException">
    /// The path escapes the root, or contains an injection-style construct (see the remarks above).
    /// </exception>
    IAsyncEnumerable<string> EnumerateFilesAsync(
        string pattern,
        string? relativeSubfolder = null,
        bool recursive = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one cleanup pass: asks the instance's cleanup strategy for expired subfolders and
    /// deletes them (path-guarded, retrying transient failures), honouring
    /// <see cref="TempFolderOptions.MaxFoldersPerPass"/>. When volume tracking is on, a
    /// low-priority measurement runs first so <see cref="TempFolderOptions.OnVolumeExceeded"/> sees
    /// current data.
    /// </summary>
    /// <remarks>
    /// The pass always yields to file activity: it does not start while a
    /// <see cref="SaveStreamAsync"/> / <see cref="CopyFileAsync"/> / <see cref="CreateSubfolder"/>
    /// is in flight (returns 0; the next pass retries), a write arriving mid-pass ends the
    /// deletion by cancellation at its next checkpoint, and a folder whose debounced activity
    /// marker has not landed yet is skipped — a just-written file is never deleted.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token; also surfaces as the pass token that an
    /// arriving write cancels (an interruption by activity does not throw — the partial count is
    /// returned instead).</param>
    /// <returns>
    /// The number of subfolders deleted by this pass. Always 0 for a read-only instance, and for a
    /// pass that was cancelled before deleting anything.
    /// </returns>
    ValueTask<int> CleanupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the root is usable: it is created when missing (read-write instances only),
    /// listing works, and — unless read-only — a probe file and a probe folder can be created,
    /// written and deleted. Called by the background service at host start so a broken root fails
    /// fast instead of at first use.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The root failed one of the access checks.</exception>
    ValueTask CheckAccessAsync(CancellationToken cancellationToken = default);
}
