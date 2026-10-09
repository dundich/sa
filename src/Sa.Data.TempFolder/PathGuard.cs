using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security;
using Microsoft.Extensions.Options;

namespace Sa.Data.TempFolder;

/// <summary>
/// Guards every path handed to a temp-folder operation: resolves it against the instance root —
/// absolute or relative — and rejects everything hostile: results that land outside the root,
/// <c>..</c> segments (even when they would stay inside), injection-style characters
/// (<c>~</c>, <c>&gt;</c>, shell metacharacters, control and invisible Unicode characters), and
/// paths that pass through a symbolic link inside the root.
/// </summary>
/// <remarks>
/// Every public entry point that takes a path goes through <see cref="Resolve"/> (or
/// <see cref="IsInside"/> for paths assembled from other sources), so a hostile or sloppy caller
/// cannot reach outside the temp root. Paths are compared with the platform's directory-separator
/// semantics: the root is always compared with a trailing separator, so a sibling directory whose
/// name merely starts with the root's name is not mistaken for a child.
/// <para>
/// <b>Rejected characters</b> (see <see cref="HasUnsafeCharacters"/>): ASCII control characters
/// (0x00–0x1F, 0x7F, including <c>\0</c>, <c>\t</c>, <c>\r</c>, <c>\n</c>), the shell/glob
/// metacharacters <c>~ &lt; &gt; | &amp; ; ` $ ^ * ? " ' %</c>, a literal <c>\</c> on Unix
/// (Windows-path smuggling), Unicode format/control characters (zero-width and bidi-override
/// tricks such as U+202E), the <c>..</c> segment on either separator, and — on Windows — a colon
/// outside the <c>C:</c> drive spec (NTFS alternate data streams). Tilde is never expanded to a
/// home directory, it is rejected outright.
/// </para>
/// <para>
/// <b>Symlinks.</b> Resolving is textual, so creation paths are additionally checked component by
/// component (<see cref="AssertNoSymlinkComponents"/>): a link planted inside the root must not
/// redirect a write to its target, wherever that target lives.
/// </para>
/// </remarks>
public static class PathGuard
{
    private static readonly StringComparison s_comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Characters rejected in every path argument. Backslash is additionally rejected on Unix,
    /// where it is a legal-but-suspicious filename character (a Windows path or a shell escape).
    /// </summary>
    private static readonly SearchValues<char> s_unsafeChars = SearchValues.Create(
        OperatingSystem.IsWindows()
            ? "~<>|&;`$^*?\"'%"
            : "~<>|&;`$^*?\"'%\\");

    /// <summary>
    /// Determines whether <paramref name="value"/> contains anything a path argument must not:
    /// unsafe characters (see the class remarks), a <c>..</c> segment, or — on Windows — a colon
    /// outside the drive spec. Used both to reject input in <see cref="Resolve"/> and to fail
    /// options validation early (e.g. for <see cref="TempFolderOptions.FolderPrefix"/>).
    /// </summary>
    /// <param name="value">The candidate string; <see langword="null"/> is treated as unsafe.</param>
    /// <returns><see langword="true"/> when the value must be rejected.</returns>
    public static bool HasUnsafeCharacters(string? value)
        => value is null || FindUnsafe(value) is not null;

    /// <summary>Returns a printable description of the first hostile construct, or <see langword="null"/> when clean.</summary>
    private static string? FindUnsafe(string value)
    {
        foreach (var c in value)
        {
            if (c < ' ' || c == '\u007F')
            {
                return $"'\\u{(int)c:X4}'"; // ASCII control, incl. \0 \t \r \n
            }

            if (s_unsafeChars.Contains(c))
            {
                return $"'{c}'";
            }

            var category = Char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Format
                or UnicodeCategory.Control
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                return $"'\\u{(int)c:X4}'"; // zero-width, bidi overrides (U+202E …), BOM, NBSP-class
            }
        }

        // Both separators on both platforms: "..\" must not slip through on Unix just because
        // the platform does not treat backslash as a separator.
        foreach (var segment in value.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return "'..'";
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var colon = value.IndexOf(':');
            if (colon >= 0)
            {
                // Only the drive spec "X:" is allowed; any other colon is an NTFS ADS vector.
                var driveSpec = colon == 1 && value.Length >= 2 && char.IsAsciiLetter(value[0]);
                if (!driveSpec || value.IndexOf(':', 2) >= 0)
                {
                    return "':'";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="rootPath"/> and returns the full
    /// path, throwing when the result would leave the root or the input is hostile.
    /// </summary>
    /// <param name="rootPath">The instance root; may be relative, it is normalised first.</param>
    /// <param name="path">
    /// A path relative to the root, or an absolute path — both are accepted as long as the
    /// resolved result is the root itself or lives under it. Empty or <c>.</c> resolves to the
    /// root. The value is never expanded: <c>~/…</c> is rejected, not rewritten to a home path.
    /// </param>
    /// <returns>The fully qualified path, guaranteed to be the root or to live under it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecurityException">
    /// The input contains an unsafe construct (see the class remarks), or the resolved path
    /// escapes the root.
    /// </exception>
    public static string Resolve(string rootPath, string path)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(path);

        var root = Path.GetFullPath(rootPath);
        var value = path.Trim();

        if (value.Length == 0 || value is ".")
        {
            return root;
        }

        // Injection checks first — before any normalisation, so a \0 or a bidi override cannot
        // hide behind GetFullPath's own (platform-dependent) behaviour.
        if (FindUnsafe(value) is { } hostile)
        {
            throw new SecurityException(
                $"Path '{path}' is rejected: {hostile} is not allowed in temp-folder paths " +
                $"(root '{root}').");
        }

        var candidate = Path.IsPathRooted(value) ? value : Path.Combine(root, value);
        var full = Path.GetFullPath(candidate);

        if (!IsInside(root, full))
        {
            throw new SecurityException(
                $"Path '{path}' resolves to '{full}', which is outside the temp root '{root}'.");
        }

        return full;
    }

    /// <summary>
    /// Determines whether <paramref name="candidate"/> is the root itself or lives under it.
    /// </summary>
    /// <param name="rootPath">The instance root; normalised with <see cref="Path.GetFullPath"/>.</param>
    /// <param name="candidate">A fully or partially qualified path to test.</param>
    /// <returns><see langword="true"/> when the candidate is inside the root.</returns>
    public static bool IsInside(string rootPath, string candidate)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(candidate);

        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(candidate);

        if (string.Equals(root, full, s_comparison))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return full.StartsWith(rootWithSeparator, s_comparison);
    }

    /// <summary>
    /// Determines whether <paramref name="path"/> is a symbolic link or junction.
    /// </summary>
    /// <remarks>
    /// A path that does not exist — or that cannot be inspected — is reported as <b>not</b> a
    /// link: there is no link to follow, and a later filesystem operation will either create the
    /// entry or fail on its own terms. <see cref="FileSystemInfo.LinkTarget"/> is used because it
    /// works for both files and directories and, unlike <c>File.Exists</c>/<c>Directory.Exists</c>,
    /// also recognises a link whose target is missing.
    /// </remarks>
    public static bool IsSymlink(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                return true;
            }

            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rejects <paramref name="absolutePath"/> when any component between
    /// <paramref name="rootPath"/> and the target — the target included — is a symbolic link or
    /// junction.
    /// </summary>
    /// <remarks>
    /// <see cref="Resolve"/> alone is not enough: it normalises the <i>textual</i> path, while the
    /// filesystem may still redirect a component through a link. <c>root/payload → /etc</c>,
    /// followed by a write to <c>root/payload/app.conf</c>, lands outside the root — no <c>..</c>
    /// required. Components that do not exist yet (about to be created) are skipped, which is why
    /// a freshly created name can never be a pre-planted link; the root itself is trusted
    /// (validated configuration) and is not probed.
    /// </remarks>
    /// <exception cref="SecurityException">
    /// A component of the path is a symlink or junction, or the path is outside the root.
    /// </exception>
    public static void AssertNoSymlinkComponents(string rootPath, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(absolutePath);

        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(absolutePath);

        if (!IsInside(root, full))
        {
            throw new SecurityException(
                $"Path '{absolutePath}' is outside the temp root '{root}'.");
        }

        var relative = Path.GetRelativePath(root, full);
        if (relative is "." or "")
        {
            return; // the root itself — trusted
        }

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            if (IsSymlink(current))
            {
                throw new SecurityException(
                    $"Path '{absolutePath}' passes through symlink '{current}'; temp-folder paths " +
                    $"must stay physically inside the root '{root}'.");
            }
        }
    }

    /// <summary>
    /// Returns the first path segment of <paramref name="absolutePath"/> relative to
    /// <paramref name="rootPath"/> — the top-level subfolder the path belongs to — or
    /// <see langword="null"/> when the path sits directly in the root.
    /// </summary>
    /// <remarks>
    /// The activity marker (debounced touch) is maintained for an accessed file's own directory
    /// and all of its ancestors up to the root — every one of those levels is aged by the nested
    /// cleanup. This method only reports the top-level segment, i.e. whether the path has any
    /// aging parent at all (a file directly in the root has none).
    /// </remarks>
    /// <exception cref="SecurityException">The path is outside the root.</exception>
    public static string? TopSegment(string rootPath, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(absolutePath);

        var root = Path.GetFullPath(rootPath);

        if (!IsInside(root, absolutePath))
        {
            throw new SecurityException(
                $"Path '{absolutePath}' is outside the temp root '{root}'.");
        }

        var relative = Path.GetRelativePath(root, Path.GetFullPath(absolutePath));

        if (relative is "." || relative.Length == 0)
        {
            return null;
        }

        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        // No separator → the path is a direct child of the root: there is no top-level
        // *subfolder* to speak of (the file lives in the root itself).
        return separator < 0 ? null : relative[..separator];
    }
}

/// <summary>
/// Validates a registered instance's normalised <see cref="TempFolderOptions"/> as part of the
/// standard options pipeline (<c>IValidateOptions</c> + <c>ValidateOnStart()</c>).
/// </summary>
/// <remarks>
/// <c>IValidateOptions</c> rather than <c>ValidateDataAnnotations()</c>: the latter is marked
/// <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT.
/// </remarks>
internal sealed class TempFolderOptionsValidator : IValidateOptions<TempFolderOptions>
{
    /// <summary>Runs <see cref="TempFolderOptions.Validate"/> and reports failures as a failed result.</summary>
    public ValidateOptionsResult Validate(string? name, TempFolderOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (ValidationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
