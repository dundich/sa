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
    /// <param name="subfolder">
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
    string CreateSubfolder(string? subfolder = null);

    /// <summary>
    /// Writes <paramref name="source"/> into a file inside the temp folder, creating parent
    /// directories as needed, and (debounced) activates the touched folder chain — the file's own
    /// directory and every ancestor up to the root. The whole write is one activity against the
    /// cleanup pass: a concurrent pass is cancelled rather than allowed to race it (see
    /// <see cref="CleanupExpiredAsync"/>). When the source's remaining length is known
    /// (<see cref="Stream.CanSeek"/>), the target file is preallocated to exactly that size so it
    /// never grows in chunks during the copy. A failed or cancelled copy removes the partially
    /// written target — no half-written file is left behind for a reader to take for complete.
    /// </summary>
    /// <param name="source">The stream to copy from; its current position is respected.</param>
    /// <param name="path">
    /// Target file path, including the file name: relative to the root or absolute — an absolute
    /// path is accepted when it resolves inside the root (see the remarks above).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The canonical relative path (relative to the root) and the absolute path of the written file.
    /// </returns>
    /// <exception cref="InvalidOperationException">The instance is read-only, or the path points at a directory.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="IOException">
    /// A file already exists at the target path and <see cref="TempFolderOptions.OverwriteFiles"/>
    /// is <see langword="false"/> — the original is left intact.
    /// </exception>
    /// <exception cref="System.Security.SecurityException">
    /// The path resolves outside the root, or contains an injection-style construct (see the remarks above).
    /// </exception>
    ValueTask<(string RelativePath, string AbsolutePath)> WriteAsync(
        Stream source, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies an external file into the temp folder. Shares the same activity exclusion as
    /// <see cref="WriteAsync"/>: the copy never overlaps a cleanup pass.
    /// </summary>
    /// <param name="sourcePath">Path of the file to copy from; must exist. Its file name must be
    /// free of injection-style characters — the name is reused inside the temp folder.</param>
    /// <param name="subfolder">
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
        string sourcePath, string? subfolder = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a file from the temp folder, handing the open stream to <paramref name="loadStream"/>.
    /// The whole read is one activity against the cleanup pass: at the moment the file is being
    /// read a concurrent pass is cancelled rather than allowed to delete under the open stream —
    /// deletion always gives way (see <see cref="CleanupExpiredAsync"/>). A successful read also
    /// counts as activity: like a write it refreshes (debounced) the last write time of the file's
    /// own directory and every ancestor up to the root, so a folder that is only ever read from
    /// does not age out. Only the open of the file may race a vanish (that reads as
    /// <see langword="false"/>); exceptions the callback itself raises — a
    /// <see cref="FileNotFoundException"/> from the caller's own code, say — propagate unchanged.
    /// Reading is allowed on a read-only instance.
    /// </summary>
    /// <param name="path">
    /// File path, including the file name: relative to the root or absolute — an absolute path
    /// is accepted when it resolves inside the root (see the remarks above).
    /// </param>
    /// <param name="loadStream">
    /// Receives the open read stream — owned by this method, disposed when the callback
    /// completes — together with <paramref name="cancellationToken"/>. Invoked once, and only
    /// when a file actually exists at <paramref name="path"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token: cancels waiting for a running cleanup pass and is passed on to
    /// <paramref name="loadStream"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a file existed at the path and was handed to
    /// <paramref name="loadStream"/>; <see langword="false"/> when there is no file there —
    /// the callback is not invoked.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The path points at a directory.</exception>
    /// <exception cref="System.Security.SecurityException">
    /// The path resolves outside the root, or contains an injection-style construct (see the remarks above).
    /// </exception>
    Task<bool> ReadAsync(
        string path,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a single <b>file</b> from the temp folder. Folders are never removed here: a path
    /// that points at a directory is rejected with <see cref="InvalidOperationException"/> —
    /// removing an expired subfolder is the cleanup pass's job (see
    /// <see cref="CleanupExpiredAsync"/>). The whole delete is one activity against the cleanup
    /// pass: at the moment the file is being removed a concurrent pass is cancelled rather than
    /// allowed to race it — deletion is a mutation, so it is allowed only on a read-write
    /// instance (see <see cref="TempFolderOptions.ReadOnly"/>).
    /// </summary>
    /// <remarks>
    /// Only the file is removed; its containing subfolder is left in place until the cleanup pass
    /// ages it out. Transient failures (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>) are retried linearly (3 attempts, 100 ms step,
    /// like <see cref="CleanupExpiredAsync"/>); if the retries are exhausted the last exception is
    /// rethrown for the caller to decide. The delete itself is idempotent — a file that vanishes
    /// under it counts as deleted.
    /// </remarks>
    /// <param name="path">
    /// File path, including the file name: relative to the root or absolute — an absolute path
    /// is accepted when it resolves inside the root (see the remarks above).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when a file existed at the path and was deleted;
    /// <see langword="false"/> when there is no file there (or it vanished concurrently).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The instance is read-only, or the path points at a directory.</exception>
    /// <exception cref="IOException">
    /// A transient failure persisted through all retry attempts.
    /// </exception>
    /// <exception cref="System.Security.SecurityException">
    /// The path resolves outside the root, or contains an injection-style construct (see the remarks above).
    /// </exception>
    Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously enumerates files matching <paramref name="pattern"/> and returns their
    /// absolute paths. The whole enumeration is one activity against the cleanup pass: a pass
    /// cannot start or keep deleting while the walk is in flight, so the caller sees a consistent
    /// snapshot of the tree (see <see cref="CleanupExpiredAsync"/>).
    /// </summary>
    /// <param name="pattern">File pattern, e.g. <c>"*.dat"</c> or <c>"report_?.csv"</c> — the
    /// pattern is a pattern, not a path, so <c>*</c>/<c>?</c> are allowed there.</param>
    /// <param name="subfolder">Subfolder to search — relative or absolute (when absolute
    /// it must still land inside the root); <see langword="null"/> or blank searches the root.</param>
    /// <param name="recursive">Whether to descend into nested subfolders. Defaults to <see langword="false"/>.</param>
    /// <param name="cancellationToken">Cancellation token; also surfaces as the iterator's token.</param>
    /// <exception cref="System.Security.SecurityException">
    /// The path escapes the root, or contains an injection-style construct (see the remarks above).
    /// </exception>
    IAsyncEnumerable<string> EnumerateFilesAsync(
        string pattern,
        string? subfolder = null,
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
    /// <see cref="WriteAsync"/> / <see cref="CopyFileAsync"/> / <see cref="ReadAsync"/> /
    /// <see cref="DeleteFileAsync"/> / <see cref="CreateSubfolder"/> /
    /// <see cref="EnumerateFilesAsync"/> is in flight (returns 0; the next pass retries), a read,
    /// write, delete or enumeration arriving mid-pass ends the deletion by cancellation at its next
    /// checkpoint, and a folder whose debounced activity marker has not landed yet is
    /// skipped — a just-accessed (written or read) file is never deleted.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token; also surfaces as the pass token that an
    /// arriving write cancels (an interruption by activity does not throw — the partial count is
    /// returned instead).</param>
    /// <returns>
    /// The number of subfolders this pass actually deleted. Always 0 for a read-only instance, for
    /// a pass that was cancelled before deleting anything, and for candidates that vanished
    /// out-of-band between selection and deletion — a vanished folder is not counted as deleted.
    /// </returns>
    ValueTask<int> CleanupExpiredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the root is usable: it is created when missing (read-write instances only),
    /// listing works, and — unless read-only — a probe file and a probe folder can be created,
    /// written and deleted. Called by the background service at host start so a broken root fails
    /// fast instead of at first use.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The root failed one of the access checks.</exception>
    ValueTask EnsureAccessAsync(CancellationToken cancellationToken = default);
}
