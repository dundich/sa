using Sa.HybridFileStorage.FileSystem;
using System.Security;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// Direct tests of the path sanitizer: the fast path, segment trimming and collapsing, the
/// platform-separator normalisation, and the portability guards (dot segments, DOS device names,
/// a trailing dot). Pure string work — no DI, no disk — so the cases the black-box suite cannot
/// reach cheaply (empty segments, both separators) are covered here.
/// </summary>
public sealed class PathSanitizerTests
{
    [Fact]
    public void Sanitize_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PathSanitizer.SanitizeRelativePath(null!));
    }

    [Fact]
    public void Sanitize_EmptyString_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => PathSanitizer.SanitizeRelativePath(string.Empty));

        Assert.Contains("File name cannot be empty", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("///")]
    public void Sanitize_WithoutAnyContent_Throws(string path)
    {
        var ex = Assert.Throws<ArgumentException>(() => PathSanitizer.SanitizeRelativePath(path));

        Assert.Contains("empty after sanitization", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData(".gitignore")]
    [InlineData("v1.0")]
    [InlineData("job_42 (final)#1")]
    public void Sanitize_SimpleName_IsReturnedUnchanged(string name)
    {
        // The fast path: one segment, already trimmed, nothing to rewrite, not a dot segment.
        Assert.Equal(name, PathSanitizer.SanitizeRelativePath(name));
    }

    [Theory]
    [InlineData("file ", "file")]
    [InlineData("  file.txt  ", "file.txt")]
    [InlineData(" a / b ", "a/b")]
    public void Sanitize_TrimsEverySegment(string path, string expected)
    {
        Assert.Equal(
            expected.Replace('/', Path.DirectorySeparatorChar),
            PathSanitizer.SanitizeRelativePath(path));
    }

    [Theory]
    [InlineData("a//b")]
    [InlineData("/a/b/")]
    [InlineData("//a///b//")]
    public void Sanitize_CollapsesEmptySegments(string path)
    {
        Assert.Equal(Path.Combine("a", "b"), PathSanitizer.SanitizeRelativePath(path));
    }

    [Theory]
    [InlineData("a\\b/c.txt")]
    [InlineData(@"a\b\c.txt")]
    [InlineData("a/b\\c.txt")]
    public void Sanitize_NormalisesBothSeparators(string path)
    {
        // A File ID is portable: whatever the caller used, the on-disk layout uses the platform
        // separator — and UploadAsync converts it back to '/' for the ID.
        Assert.Equal(Path.Combine("a", "b", "c.txt"), PathSanitizer.SanitizeRelativePath(path));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/./b")]
    [InlineData("a/b/..")]
    [InlineData("..\\escape.txt")]
    public void Sanitize_DotSegmentAnywhere_IsRejected(string path)
    {
        Assert.Throws<SecurityException>(() => PathSanitizer.SanitizeRelativePath(path));
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("lpt9.bin")]
    [InlineData("sub/CON.log")]
    public void Sanitize_ReservedDeviceName_IsRejected(string path)
    {
        Assert.Throws<SecurityException>(() => PathSanitizer.SanitizeRelativePath(path));
    }

    [Theory]
    [InlineData("report.txt.")]
    [InlineData("...")]
    [InlineData("sub/dir.")]
    public void Sanitize_TrailingDot_IsRejected(string path)
    {
        Assert.Throws<SecurityException>(() => PathSanitizer.SanitizeRelativePath(path));
    }

    [Fact]
    public void Sanitize_PlatformInvalidCharacter_IsReplacedWithUnderscore()
    {
        // The only place the replacement rule is observable on Unix is NUL (the sole invalid
        // filename char there); on Windows any of the reserved characters would do. Pick the
        // first one the platform flags, skipping the separators the sanitizer splits on first.
        var invalid = Path.GetInvalidFileNameChars().First(c => c is not '/' and not '\\');

        Assert.Equal("a_b.txt", PathSanitizer.SanitizeRelativePath($"a{invalid}b.txt"));
    }
}
