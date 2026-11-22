using Sa.Data.TempFolder;
using System.Security;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// The path guard: every path resolves inside the root whether given as relative or absolute,
/// and nothing hostile gets through — <c>..</c> segments (even ones that would stay inside),
/// sibling directories that merely share the root's name prefix, and injection-style characters
/// (<c>~</c>, <c>&gt;</c>, shell metacharacters, control and invisible Unicode characters).
/// </summary>
public sealed class PathGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sa_tf_guard");

    [Fact]
    public void Resolve_RelativePath_LandsUnderRoot()
    {
        var resolved = PathGuard.Resolve(Root, Path.Combine("sub", "file.txt"));

        Assert.Equal(Path.Combine(Path.GetFullPath(Root), "sub", "file.txt"), resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    public void Resolve_EmptyOrDot_ReturnsTheRoot(string relative)
    {
        Assert.Equal(Path.GetFullPath(Root), PathGuard.Resolve(Root, relative));
    }

    [Fact]
    public void Resolve_DotDotSegment_IsRejected()
    {
        Assert.Throws<SecurityException>(() => PathGuard.Resolve(Root, Path.Combine("..", "escape.txt")));
        Assert.Throws<SecurityException>(() => PathGuard.Resolve(Root, Path.Combine("a", "..", "..", "escape.txt")));
    }

    [Fact]
    public void Resolve_DotDotSegmentStayingInsideTheRoot_IsStillRejected()
    {
        // "a/../b" would normalise to "b" — inside the root and harmless — but ".." is treated as
        // an injection attempt outright: no caller needs it in a temp path.
        Assert.Throws<SecurityException>(() => PathGuard.Resolve(Root, Path.Combine("a", "..", "b")));
        Assert.Throws<SecurityException>(() => PathGuard.Resolve(Root, "a/../b"));
    }

    [Fact]
    public void Resolve_RootedPathOutsideRoot_IsRejected()
    {
        // Absolute input is legitimate (see the test below), but only when it lands inside the root.
        Assert.Throws<SecurityException>(() => PathGuard.Resolve(Root, Path.GetTempPath()));
        Assert.Throws<SecurityException>(
            () => PathGuard.Resolve(Root, Path.Combine(Path.GetTempPath(), "sa_tf_guard_evil", "x.txt")));
    }

    [Fact]
    public void Resolve_RootedPathInsideRoot_IsAccepted()
    {
        var absolute = Path.Combine(Path.GetFullPath(Root), "sub", "file.txt");

        Assert.Equal(absolute, PathGuard.Resolve(Root, absolute));
    }

    [Theory]
    [InlineData('~')] // shell/home expansion — never expanded, rejected
    [InlineData('>')] // redirection
    [InlineData('<')] // redirection
    [InlineData('|')] // pipe
    [InlineData('&')] // command chaining
    [InlineData(';')] // command chaining
    [InlineData('`')] // command substitution
    [InlineData('$')] // variable expansion
    [InlineData('^')] // cmd.exe escape
    [InlineData('*')] // glob
    [InlineData('?')] // glob
    [InlineData('"')] // quote breaking
    [InlineData('\'')] // quote breaking
    [InlineData('%')] // Windows env expansion / percent-encoded traversal ("..%2f")
    [InlineData('\0')] // NUL truncation
    [InlineData('\t')] // control
    [InlineData('\n')] // control
    [InlineData('\r')] // control
    [InlineData('\u202E')] // bidi override (RLO): visual filename spoofing
    [InlineData('\u200B')] // zero-width space: invisible construct hiding
    public void Resolve_UnsafeCharacter_IsRejected(char hostile)
    {
        Assert.Throws<SecurityException>(() => PathGuard.Resolve(Root, $"sub/file{hostile}name.txt"));
    }

    [Fact]
    public void Resolve_BackslashSegmentOnUnix_IsRejected()
    {
        // On Unix '\' is a legal filename character, but a backslash-separated "..\" segment is a
        // Windows-style traversal smuggled into a Unix path (and a shell escape) — rejected anyway.
        if (OperatingSystem.IsWindows())
        {
            return; // on Windows '\' is the normal separator, covered by the ".." tests above
        }

        Assert.Throws<SecurityException>(() => PathGuard.Resolve(Root, "..\\escape.txt"));
        Assert.Throws<SecurityException>(() => PathGuard.Resolve(Root, "sub\\file.txt"));
    }

    [Fact]
    public void HasUnsafeCharacters_DetectsHostileValues()
    {
        Assert.True(PathGuard.HasUnsafeCharacters(null));
        Assert.True(PathGuard.HasUnsafeCharacters("a~b"));
        Assert.True(PathGuard.HasUnsafeCharacters("a>b"));
        Assert.True(PathGuard.HasUnsafeCharacters(".."));
        Assert.True(PathGuard.HasUnsafeCharacters(Path.Combine("a", "..", "b")));

        Assert.False(PathGuard.HasUnsafeCharacters(string.Empty));
        Assert.False(PathGuard.HasUnsafeCharacters("job_42 (final)#1"));
    }

    [Fact]
    public void Resolve_NullIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => PathGuard.Resolve(Root, null!));
        Assert.Throws<ArgumentNullException>(() => PathGuard.Resolve(null!, "sub"));
    }

    [Fact]
    public void IsInside_RootItself_IsInside()
    {
        Assert.True(PathGuard.IsInside(Root, Root));
    }

    [Fact]
    public void IsInside_SiblingSharingTheRootName_IsOutside()
    {
        // "/tmp/sa_tf" must not swallow "/tmp/sa_tf_evil": the comparison includes the
        // directory separator, so a name prefix is never mistaken for a parent.
        Assert.False(PathGuard.IsInside(Root, Root + "_evil"));
        Assert.False(PathGuard.IsInside(Root, Root + "_evil" + Path.DirectorySeparatorChar + "x.txt"));
    }

    [Fact]
    public void TopSegment_ReturnsTheFirstSegment()
    {
        var absolute = Path.Combine(Path.GetFullPath(Root), "a", "b", "c.txt");

        Assert.Equal("a", PathGuard.TopSegment(Root, absolute));
    }

    [Fact]
    public void TopSegment_FileDirectlyInRoot_ReturnsNull()
    {
        var absolute = Path.Combine(Path.GetFullPath(Root), "file.txt");

        Assert.Null(PathGuard.TopSegment(Root, absolute));
    }

    [Fact]
    public void TopSegment_OutsideRoot_IsRejected()
    {
        Assert.Throws<SecurityException>(
            () => PathGuard.TopSegment(Root, Path.Combine(Path.GetTempPath(), "elsewhere", "x.txt")));
    }

    [Fact]
    public void IsSymlink_RecognisesADanglingLink()
    {
        var root = TestTemp.NewRoot();

        try
        {
            var link = Path.Combine(root, "link");
            TestTemp.CreateDirectoryLink(link, Path.Combine(root, "missing-target"));

            // Nothing exists at the target, so File/Directory.Exists answer false — but the entry
            // is a link, and a create/write through it would land wherever the target points.
            Assert.True(PathGuard.IsSymlink(link));
            Assert.False(Directory.Exists(link));
        }
        finally
        {
            TestTemp.TryDelete(root);
        }
    }

    [Fact]
    public void IsSymlink_RecognisesAFileLink()
    {
        var root = TestTemp.NewRoot();

        try
        {
            var target = Path.Combine(root, "target.bin");
            File.WriteAllText(target, "x");
            var link = Path.Combine(root, "link.bin");
            TestTemp.CreateFileLink(link, target);

            Assert.True(PathGuard.IsSymlink(link));
            Assert.True(PathGuard.IsSymlink(target) is false);
        }
        finally
        {
            TestTemp.TryDelete(root);
        }
    }

    [Fact]
    public void IsSymlink_MissingPath_IsFalse()
    {
        // Nothing to follow: a plain missing entry is not a link, and the operation that follows
        // will either create it or fail on its own terms.
        Assert.False(PathGuard.IsSymlink(Path.Combine(TestTemp.NewMissingPath(), "nope")));
    }

    [Fact]
    public void AssertNoSymlinkComponents_OutsideRoot_Throws()
    {
        Assert.Throws<SecurityException>(
            () => PathGuard.AssertNoSymlinkComponents(
                Root, Path.Combine(Path.GetTempPath(), "elsewhere", "x.txt")));
    }

    [Fact]
    public void AssertNoSymlinkComponents_RootItself_IsAllowed()
    {
        // The root is validated configuration, not probed: a root that happens to be a link (a
        // mounted symlinked temp dir) must not make every operation fail.
        PathGuard.AssertNoSymlinkComponents(Root, Root);
    }
}
