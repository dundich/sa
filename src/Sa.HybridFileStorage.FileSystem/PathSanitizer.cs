using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;

namespace Sa.HybridFileStorage.FileSystem;

internal static class PathSanitizer
{
    private static readonly SearchValues<char> s_separators = SearchValues.Create(['/', '\\']);
    private static readonly SearchValues<char> s_invalidChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    public static string SanitizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (relativePath.Length == 0)
        {
            throw new ArgumentException("File name cannot be empty.", nameof(relativePath));
        }

        if (IsSimplePath(relativePath))
        {
            return SanitizeSimplePath(relativePath);
        }

        return SanitizeComplexPath(relativePath);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSimplePath(ReadOnlySpan<char> path)
    {
        foreach (var c in path)
        {
            if (c is '/' or '\\' or '.' or '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                return false;
            }
        }
        return true;
    }

    private static string SanitizeSimplePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

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

        if (!ContainsInvalidChars(trimmed))
        {
            return trimmed.ToString();
        }

        return ReplaceInvalidChars(trimmed);
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
