using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder.FilePathResolver;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// The keyed DI registration: resolve by name (or the default key), one singleton per key,
/// duplicate names rejected at registration, the file-system seam swappable, and a missing root
/// surfacing at first resolve — never at registration.
/// </summary>
public class FilePathResolverSetupTests
{
    /// <summary>Synthetic root for registrations whose file system comes from a fake.</summary>
    private const string FakeRoot = "/mikopbx";

    [Fact]
    public void AddFilePathResolver_Keyed_ResolvesTheConfiguredSingleton()
    {
        using var root = new TempRoot();
        var services = new ServiceCollection();

        services.AddFilePathResolver("records", root.Root, r => r.WithDeepSearch(true));

        using var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredKeyedService<IFilePathResolver>("records");
        var same = provider.GetRequiredKeyedService<IFilePathResolver>("records");

        Assert.Same(resolver, same); // one singleton per key
        var concrete = Assert.IsType<FilePathResolver>(resolver);
        Assert.True(concrete.IsDeepSearch); // configure ran on the instance
        Assert.Equal(root.Root, concrete.ConfiguredPath);
    }

    [Fact]
    public void AddFilePathResolver_WithoutAName_UsesTheDefaultKey()
    {
        using var root = new TempRoot();
        var services = new ServiceCollection();

        services.AddFilePathResolver(root.Root);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<FilePathResolver>(
            provider.GetRequiredKeyedService<IFilePathResolver>(Setup.DefaultName));
    }

    [Fact]
    public void AddFilePathResolver_DuplicateName_ThrowsAtRegistration()
    {
        using var root = new TempRoot();
        var services = new ServiceCollection();

        services.AddFilePathResolver("dup", root.Root);

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddFilePathResolver("dup", root.Root));

        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFilePathResolver_KeyedFileSystemService_IsUsedInsteadOfTheDisk()
    {
        // The root does not exist on disk — the keyed fake vouches for everything under it.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IFileSystemService>("fake", new FakeFileSystem("/mikopbx/core.mp3"));
        services.AddFilePathResolver("fake", FakeRoot);

        using var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredKeyedService<IFilePathResolver>("fake");

        Assert.Equal(Platform("/mikopbx/core.mp3"), resolver.ResolvePath("core.mp3"));
    }

    [Fact]
    public void AddFilePathResolver_MissingRoot_ThrowsOnFirstResolve_NotAtRegistration()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sa_fpr_di_" + Path.GetRandomFileName());
        var services = new ServiceCollection();

        services.AddFilePathResolver("late", missing); // registration itself succeeds

        using var provider = services.BuildServiceProvider();

        Assert.Throws<DirectoryNotFoundException>(
            () => provider.GetRequiredKeyedService<IFilePathResolver>("late"));
    }

    [Fact]
    public void AddFilePathResolver_InvalidArguments_Throw()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddFilePathResolver("x", "/root"));
        Assert.ThrowsAny<ArgumentException>(() => services.AddFilePathResolver("  ", "/root"));
        Assert.ThrowsAny<ArgumentException>(() => services.AddFilePathResolver("x", "   "));
    }

    /// <summary>Converts a '/'-separated path literal to the platform's separators.</summary>
    private static string Platform(string path)
        => path.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// In-memory <see cref="IFileSystemService"/> over a fixed set of file paths; the root
    /// always "exists", every probe is answered from memory.
    /// </summary>
    private sealed class FakeFileSystem(params string[] files) : IFileSystemService
    {
        private readonly HashSet<string> _files = [.. files.Select(Key)];

        public bool FileExists(string path) => _files.Contains(Key(path));

        public bool DirectoryExists(string path) => true;

        // No enumeration in these tests — deep search stays off.
        public IEnumerable<string> EnumerateFiles(string directory, string pattern)
            => _files
                .Where(f => f.StartsWith(Key(directory) + "/", StringComparison.Ordinal))
                .Select(f => f.Replace('/', Path.DirectorySeparatorChar));

        private static string Key(string path)
            => path.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>A throw-away directory under the system temp path, removed on dispose.</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "sa_fpr_di_" + Path.GetRandomFileName());
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

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
