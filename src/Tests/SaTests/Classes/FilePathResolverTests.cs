using Sa.Classes;
using System.Collections.Concurrent;

namespace SaTests.Classes;

public class FilePathResolverTests
{
    /// <summary>Synthetic root used together with <see cref="FakeFileSystem"/> (no real directories needed).</summary>
    private const string FakeRoot = "/mikopbx";

    [Theory]
    // Literal absolute path that exists on this machine → Direct.
    [InlineData(
        "/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/has-root.mp3",
        "/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/has-root.mp3")]
    // Unrooted path without leading slash → looked up relative to the configured root.
    [InlineData(
        "storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/no-root.mp3",
        "/mikopbx/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/no-root.mp3")]
    // Date-only path → lives directly under the configured root.
    [InlineData(
        "2021/06/16/15/relative.mp3",
        "/mikopbx/2021/06/16/15/relative.mp3")]
    // Absolute path with the file system root as directory → falls back to the configured root.
    [InlineData("/core.mp3", "/mikopbx/core.mp3")]
    // Bare file name → looked up in the configured root.
    [InlineData("core-1.mp3", "/mikopbx/core-1.mp3")]
    public void ResolvePath_FuzzyDatabasePath_ResolvesExpectedPath(string dbPath, string expectedPath)
    {
        // Arrange
        var fake = new FakeFileSystem(
            "/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/has-root.mp3",
            "/mikopbx/storage/usbdisk1/mikopbx/astspool/monitor/2021/06/16/15/no-root.mp3",
            "/mikopbx/2021/06/16/15/relative.mp3",
            "/mikopbx/core.mp3",
            "/mikopbx/core-1.mp3");
        var resolver = new FilePathResolver(fake, FakeRoot);
        var expected = Platform(expectedPath);

        // Act
        var first = resolver.ResolvePath(dbPath);
        var second = resolver.ResolvePath(dbPath); // exercises the cached mapping

        // Assert
        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.True(resolver.IsResolved);
    }

    [Fact]
    public void ResolvePath_SecondLookup_UsesCachedMappingWithSingleProbe()
    {
        // Arrange — the cascade needs several probes before it finds the suffix mapping
        var fake = new FakeFileSystem("/mikopbx/A/2024/a.mp3");
        var resolver = new FilePathResolver(fake, FakeRoot);
        const string dbPath = "/db/A/2024/a.mp3";
        var expected = Platform("/mikopbx/A/2024/a.mp3");

        // Act
        var first = resolver.ResolvePath(dbPath);
        var probesAfterFirst = fake.Probes;

        fake.Probes = 0;
        var second = resolver.ResolvePath(dbPath);
        var probesAfterSecond = fake.Probes;

        // Assert — the second call is a dictionary read plus exactly one file probe
        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.True(probesAfterFirst > 1, $"Cascade expected several probes, got {probesAfterFirst}.");
        Assert.Equal(1, probesAfterSecond);
    }

    [Fact]
    public void ResolvePath_MultipleDatabasePrefixes_EachResolvesToItsOwnFile()
    {
        // Arrange
        using var scope = new TempRoot();
        scope.Touch(Path.Combine("A", "2024", "a.mp3"));
        scope.Touch(Path.Combine("B", "2025", "b.mp3"));
        var resolver = FilePathResolver.Create(scope.Root);

        // Act — two different per-file mappings side by side, each resolved twice
        var a1 = resolver.ResolvePath("/db/A/2024/a.mp3");
        var b1 = resolver.ResolvePath("/db/B/2025/b.mp3");
        var a2 = resolver.ResolvePath("/db/A/2024/a.mp3");
        var b2 = resolver.ResolvePath("/db/B/2025/b.mp3");

        // Assert
        Assert.Equal(Path.Combine(scope.Root, "A", "2024", "a.mp3"), a1);
        Assert.Equal(Path.Combine(scope.Root, "B", "2025", "b.mp3"), b1);
        Assert.Equal(a1, a2);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public void ResolvePath_DeepSearchHit_IsNotCached_AndDoesNotMarkResolved()
    {
        // Arrange
        var fake = new FakeFileSystem("/mikopbx/deep/nested/deepfile.mp3");
        var resolver = new FilePathResolver(fake, FakeRoot).WithDeepSearch(true);
        var expected = Platform("/mikopbx/deep/nested/deepfile.mp3");

        // Act
        var first = resolver.ResolvePath("deepfile.mp3");
        var second = resolver.ResolvePath("deepfile.mp3");
        var enumerationsAfterTwoCalls = fake.Enumerations;
        var missing = resolver.ResolvePath("other.mp3");

        // Assert — deep hits carry no directory mapping: re-enumerated each time, never cached
        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Equal(2, enumerationsAfterTwoCalls);
        Assert.False(resolver.IsResolved);
        Assert.Null(missing);
    }

    [Fact]
    public void WithForceSearch_ReRunsCascade_PrefersLiteralPathOverCachedMapping()
    {
        // Arrange — a directory outside the configured root; the literal path does not exist yet
        var outside = Path.Combine(Path.GetTempPath(), "sa_fpr_lit_" + Path.GetRandomFileName());
        Directory.CreateDirectory(outside);
        var literalDbPath = Path.Combine(outside, "lit.mp3");

        using var scope = new TempRoot();
        var leaf = Path.GetFileName(outside);
        scope.Touch(Path.Combine(leaf, "lit.mp3")); // resolvable only via the directory-suffix walk
        var mappedExpected = Path.Combine(scope.Root, leaf, "lit.mp3");
        var resolver = FilePathResolver.Create(scope.Root);

        try
        {
            // Act
            var mapped = resolver.ResolvePath(literalDbPath);
            var cached = resolver.ResolvePath(literalDbPath);

            File.WriteAllText(literalDbPath, "x"); // the literal path appears
            var withoutForce = resolver.ResolvePath(literalDbPath);
            resolver.WithForceSearch(true);
            var withForce = resolver.ResolvePath(literalDbPath);

            // Assert — without force the still-valid cached mapping wins,
            // with force the cascade re-runs and the literal path (higher priority) is found
            Assert.Equal(mappedExpected, mapped);
            Assert.Equal(mappedExpected, cached);
            Assert.Equal(mappedExpected, withoutForce);
            Assert.Equal(literalDbPath, withForce);
            Assert.True(resolver.IsForceSearchEnabled);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void ResolvePath_AsRelativePath_RelativizesUnderRoot_AndKeepsAbsolutePathOutsideRoot()
    {
        // Arrange
        using var scope = new TempRoot();
        scope.Touch(Path.Combine("2021", "06", "x.mp3"));
        var dbPath = Path.Combine("2021", "06", "x.mp3");
        var resolver = FilePathResolver.Create(scope.Root);

        var outsideFile = Path.Combine(Path.GetTempPath(), "sa_fpr_out_" + Path.GetRandomFileName() + ".mp3");
        File.WriteAllText(outsideFile, "x");

        try
        {
            // Act
            var relative = resolver.ResolvePath(dbPath, asRelativePath: true);
            var absolute = resolver.ResolvePath(dbPath);
            var outside = resolver.ResolvePath(outsideFile, asRelativePath: true);

            // Assert
            Assert.Equal(dbPath, relative);
            Assert.Equal(Path.Combine(scope.Root, "2021", "06", "x.mp3"), absolute);
            Assert.Equal(outsideFile, outside); // outside the root: an absolute path stays absolute
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public void ResolvePath_WithPossibleExtensions_FindsFileWithDifferentExtension()
    {
        // Arrange — the database says .mp3, the real file is .wav
        using var scope = new TempRoot();
        scope.Touch("core.wav");
        var resolver = FilePathResolver.Create(scope.Root).WithPossibleExtensions("mp3", ".wav");

        // Act
        var resolved = resolver.ResolvePath("core.mp3");

        // Assert
        Assert.Equal([".mp3", ".wav"], resolver.PossibleExtensions);
        Assert.Equal(Path.Combine(scope.Root, "core.wav"), resolved);
    }

    [Fact]
    public void ResolvePath_CacheEntryBecomesStale_ReSearchesAndRecoversViaDeepSearch()
    {
        // Arrange
        using var scope = new TempRoot();
        scope.Touch(Path.Combine("A", "x.mp3"));
        var resolver = FilePathResolver.Create(scope.Root);
        const string dbPath = "/db/A/x.mp3";

        // Act
        var mapped = resolver.ResolvePath(dbPath);

        Directory.CreateDirectory(Path.Combine(scope.Root, "B"));
        File.Move(Path.Combine(scope.Root, "A", "x.mp3"), Path.Combine(scope.Root, "B", "x.mp3"));
        var withoutDeep = resolver.ResolvePath(dbPath); // cached mapping no longer resolves

        resolver.WithDeepSearch(true);
        var withDeep = resolver.ResolvePath(dbPath);

        // Assert
        Assert.Equal(Path.Combine(scope.Root, "A", "x.mp3"), mapped);
        Assert.Null(withoutDeep);
        Assert.Equal(Path.Combine(scope.Root, "B", "x.mp3"), withDeep);
    }

    [Fact]
    public void ResolvePath_FileMissing_ReturnsNull_AndIsFoundAfterCreation()
    {
        // Arrange
        using var scope = new TempRoot();
        var resolver = FilePathResolver.Create(scope.Root);

        // Act
        var missing = resolver.ResolvePath("ghost.mp3");
        scope.Touch("ghost.mp3");
        var found = resolver.ResolvePath("ghost.mp3");

        // Assert — a miss is not cached, so the next lookup searches again
        Assert.Null(missing);
        Assert.Equal(Path.Combine(scope.Root, "ghost.mp3"), found);
    }

    [Fact]
    public void WithMap_ExplicitGlobalMapping_ResolvesWithoutCascade()
    {
        // Arrange — decoy at the path a naive prefix strip would probe ("usbdisk10" → "0/f.mp3")
        using var scope = new TempRoot();
        scope.Touch("f.mp3");
        scope.Touch(Path.Combine("0", "f.mp3"));
        var resolver = FilePathResolver.Create(scope.Root).WithMap("/archive/usbdisk1");

        // Act
        var mapped = resolver.ResolvePath("/archive/usbdisk1/f.mp3");
        var mappedAgain = resolver.ResolvePath("/archive/usbdisk1/f.mp3");
        var siblingPrefix = resolver.ResolvePath("/archive/usbdisk10/f.mp3");

        // Assert
        Assert.Equal(Path.Combine(scope.Root, "f.mp3"), mapped);
        Assert.Equal(Path.Combine(scope.Root, "f.mp3"), mappedAgain);
        Assert.Null(siblingPrefix); // "usbdisk10" is not a child of "usbdisk1"
        Assert.True(resolver.IsResolved);
    }

    [Fact]
    public void Resolved_FiresExactlyOnce_AcrossManyLookups()
    {
        // Arrange
        using var scope = new TempRoot();
        scope.Touch("a.mp3");
        scope.Touch("b.mp3");
        var resolver = FilePathResolver.Create(scope.Root);

        var fired = 0;
        resolver.Resolved += (_, _) => Interlocked.Increment(ref fired);

        // Act
        resolver.ResolvePath("a.mp3");
        resolver.ResolvePath("b.mp3");
        resolver.ResolvePath("a.mp3");

        // Assert
        Assert.Equal(1, fired);
        Assert.True(resolver.IsResolved);
    }

    [Fact]
    public void ResolvePath_ConcurrentLookups_AllResolveAndEventFiresOnce()
    {
        // Arrange
        using var scope = new TempRoot();
        scope.Touch(Path.Combine("A", "2024", "a.mp3"));
        scope.Touch(Path.Combine("B", "2025", "b.mp3"));
        var resolver = FilePathResolver.Create(scope.Root);

        var fired = 0;
        resolver.Resolved += (_, _) => Interlocked.Increment(ref fired);

        var expectedA = Path.Combine(scope.Root, "A", "2024", "a.mp3");
        var expectedB = Path.Combine(scope.Root, "B", "2025", "b.mp3");
        var results = new ConcurrentBag<string?>();

        // Act
        Parallel.For(0, 200, i =>
        {
            var dbPath = i % 2 == 0 ? "/db/A/2024/a.mp3" : "/db/B/2025/b.mp3";
            results.Add(resolver.ResolvePath(dbPath));
        });

        // Assert
        Assert.Equal(100, results.Count(r => r == expectedA));
        Assert.Equal(100, results.Count(r => r == expectedB));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ResolvePath_NullOrWhiteSpace_ThrowsArgumentException()
    {
        // Arrange
        var resolver = new FilePathResolver(new FakeFileSystem(), FakeRoot);

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => resolver.ResolvePath(null!));
        Assert.ThrowsAny<ArgumentException>(() => resolver.ResolvePath(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => resolver.ResolvePath("   "));
    }

    [Fact]
    public void Create_MissingDirectory_ThrowsDirectoryNotFoundException()
    {
        // Arrange
        var missing = Path.Combine(Path.GetTempPath(), "sa_fpr_missing_" + Path.GetRandomFileName());

        // Act & Assert
        Assert.Throws<DirectoryNotFoundException>(() => FilePathResolver.Create(missing));
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        // Arrange
        using var scope = new TempRoot();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new FilePathResolver(null!, scope.Root));
        Assert.Throws<ArgumentException>(() => new FilePathResolver(new FakeFileSystem(), "   "));
    }

    /// <summary>Converts a '/'-separated path literal to the platform's separators.</summary>
    private static string Platform(string path)
        => path.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// In-memory <see cref="IFileSystemService"/> over a fixed set of file paths.
    /// Probes are counted so tests can assert cache behaviour.
    /// </summary>
    private sealed class FakeFileSystem(params string[] files) : IFileSystemService
    {
        private readonly HashSet<string> _files = [.. files.Select(Key)];

        /// <summary>Number of <see cref="FileExists"/> probes.</summary>
        public int Probes;

        /// <summary>Number of <see cref="EnumerateFiles"/> calls.</summary>
        public int Enumerations;

        public bool FileExists(string path)
        {
            Interlocked.Increment(ref Probes);
            return _files.Contains(Key(path));
        }

        public bool DirectoryExists(string path) => true;

        public IEnumerable<string> EnumerateFiles(string directory, string pattern)
        {
            Interlocked.Increment(ref Enumerations);

            var prefix = Key(directory) + "/";
            return _files
                .Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && MatchName(Path.GetFileName(f), pattern))
                .Select(f => f.Replace('/', Path.DirectorySeparatorChar))
                .ToList();
        }

        private static bool MatchName(string name, string pattern)
            => pattern.Contains('*')
                ? name.StartsWith(pattern[..pattern.IndexOf('*')], StringComparison.Ordinal)
                : name == pattern;

        private static string Key(string path)
            => path.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>A throw-away directory under the system temp path, removed on dispose.</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "sa_fpr_" + Path.GetRandomFileName());
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Touch(string relativePath)
        {
            var full = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
