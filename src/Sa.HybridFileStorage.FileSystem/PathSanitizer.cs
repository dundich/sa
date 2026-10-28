using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;

namespace Sa.HybridFileStorage.FileSystem;

internal static class PathSanitizer
{
    private static readonly SearchValues<char> s_separators = SearchValues.Create('/', '\\');
    private static readonly SearchValues<char> s_invalidChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    public static string SanitizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (relativePath.Length == 0)
        {
            throw new ArgumentException("File name cannot be empty.", nameof(relativePath));
        }

        // Fast path: the complex path leaves such a name exactly as it is (a single segment,
        // already trimmed, no character it would rewrite, and not a dot segment), so there is
        // nothing to build. Kept in sync with SanitizeComplexPath deliberately — a name that
        // reaches the slow path must be handled there, never returned unchanged by mistake.
        if (IsSimplePath(relativePath))
        {
            return relativePath;
        }

        return SanitizeComplexPath(relativePath);
    }

    /// <summary>
    /// Determines whether <paramref name="relativePath"/> is a name the complex path would return
    /// unchanged: no separators, no leading/trailing whitespace (which the path trims), no character
    /// <c>Path.GetInvalidFileNameChars()</c> flags (which the path replaces with <c>'_'</c>), and not
    /// a <c>"."</c> / <c>".."</c> segment (which the path rejects).
    /// </summary>
    /// <remarks>
    /// The dot segments need the explicit check: without a separator the whole name is one segment,
    /// so <c>".."</c> would otherwise be returned untouched and bypass the traversal guard.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSimplePath(ReadOnlySpan<char> path)
    {
        if (char.IsWhiteSpace(path[0]) || char.IsWhiteSpace(path[^1]))
        {
            return false;
        }

        foreach (var c in path)
        {
            if (IsSeparator(c) || s_invalidChars.Contains(c))
            {
                return false;
            }
        }

        // The same checks the complex path refuses with: a dot segment, a DOS device name, and a
        // segment Windows would strip a trailing dot from. Everything else is returned as is.
        return !path.Equals("..", StringComparison.Ordinal)
            && !path.Equals(".", StringComparison.Ordinal)
            && !IsReservedDeviceName(path)
            && path[^1] != '.';
    }

    private static string SanitizeComplexPath(string relativePath)
    {
        ReadOnlySpan<char> span = relativePath.AsSpan();

        var estimatedCapacity = span.Length + 16;
        var builder = new StringBuilder(estimatedCapacity);

        var currentPartStart = 0;
        var hasContent = false;

        for (var i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || IsSeparator(span[i]))
            {
                if (i > currentPartStart)
                {
                    var part = span[currentPartStart..i];
                    var sanitized = SanitizePathPart(part);

                    if (sanitized.Length > 0)
                    {
                        if (hasContent)
                        {
                            builder.Append(Path.DirectorySeparatorChar);
                        }
                        builder.Append(sanitized);
                        hasContent = true;
                    }
                }

                currentPartStart = i + 1;
            }
        }

        if (!hasContent)
        {
            throw new ArgumentException("""
Resulting path is empty after sanitization. 
Ensure the filename contains valid characters.
""", nameof(relativePath));
        }

        return builder.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSeparator(char c) => s_separators.Contains(c);


    private static string SanitizePathPart(ReadOnlySpan<char> part)
    {

        var trimmed = part.Trim();

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.Equals("..", StringComparison.Ordinal) ||
            trimmed.Equals(".", StringComparison.Ordinal))
        {
            throw new SecurityException($"""
Path segment '{trimmed}' is not allowed. 
Relative paths (.. / .) are prohibited for security reasons.
""");
        }

        EnsurePortableName(trimmed);

        if (!ContainsInvalidChars(trimmed))
        {
            return trimmed.ToString();
        }

        return ReplaceInvalidChars(trimmed);
    }

    /// <summary>
    /// Rejects the segments Windows cannot represent as themselves: a reserved device name and a
    /// segment with a trailing dot.
    /// </summary>
    /// <remarks>
    /// Both are checked on <b>every</b> platform, not only under Windows. A File ID is a portable
    /// identifier (it always uses <c>/</c>), so an ID minted on Linux must keep meaning the same
    /// file when the storage is read on a Windows host — and there <c>NUL.txt</c> resolves to the
    /// null device instead of a file, while <c>report.txt.</c> silently aliases <c>report.txt</c>
    /// (Win32 strips the trailing dot), so two File IDs would fight over one file.
    /// </remarks>
    private static void EnsurePortableName(ReadOnlySpan<char> segment)
    {
        if (IsReservedDeviceName(segment))
        {
            throw new SecurityException(
                $"Path segment '{segment}' is a reserved device name on Windows and is not allowed.");
        }

        if (segment[^1] == '.')
        {
            throw new SecurityException(
                $"Path segment '{segment}' must not end with a dot: Windows strips a trailing dot, " +
                "so the file would alias another File ID.");
        }
    }

    /// <summary>
    /// Gets a value indicating whether <paramref name="segment"/> names a DOS device. Win32 matches
    /// the stem — the text before the first dot — case-insensitively and ignores the extension, so
    /// <c>NUL</c>, <c>nul</c> and <c>NUL.txt</c> all resolve to the device.
    /// </summary>
    private static bool IsReservedDeviceName(ReadOnlySpan<char> segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];

        if (stem.Length == 0)
        {
            return false;
        }

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // COM0-COM9 / LPT0-LPT9 (the documented range is 1-9; 0 is matched as well, because some
        // Windows versions resolve it too, and rejecting a legal-looking name is the safe side).
        return stem.Length == 4
            && char.IsAsciiDigit(stem[3])
            && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsInvalidChars(ReadOnlySpan<char> span)
        => span.IndexOfAny(s_invalidChars) >= 0;


    private static string ReplaceInvalidChars(ReadOnlySpan<char> span)
    {
        Span<char> buffer = span.Length <= 256
            ? stackalloc char[span.Length]
            : new char[span.Length];

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            buffer[i] = s_invalidChars.Contains(c) ? '_' : c;
        }

        return new string(buffer);
    }
}
