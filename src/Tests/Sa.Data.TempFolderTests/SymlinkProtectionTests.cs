using System.Security;
using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Symlink escape protection: cleanup neither descends into nor deletes a linked folder, create /
/// write reject any path whose components pass through a link, and the volume scan never follows
/// one. A link planted inside the root must not redirect an operation outside it.
/// </summary>
public sealed class SymlinkProtectionTests : IDisposable
{
    private const string Key = "symlink";

    private readonly string _root = TestTemp.NewRoot();
    private readonly string _outside = TestTemp.NewRoot();

    public void Dispose()
    {
        TestTemp.TryDelete(_root);
        TestTemp.TryDelete(_outside);
    }

    private ServiceProvider Build(Action<TempFolderOptions>? options = null)
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder(Key, builder => builder.Options(ob => ob.Configure(o =>
        {
            o.RootPath = _root;
            options?.Invoke(o);
        })));
        return services.BuildServiceProvider();
    }

    private ITempFolder Resolve(ServiceProvider provider)
        => provider.GetRequiredKeyedService<ITempFolder>(Key);

    /// <summary>
    /// Creates a directory symlink, or skips the test where the platform forbids it (Windows
    /// without developer mode / the create-symlink privilege).
    /// </summary>
    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Directory symlinks are unavailable on this platform: {ex.Message}");
        }
    }

    /// <summary>Same as <see cref="CreateDirectoryLink"/>, for a file link.</summary>
    private static void CreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"File symlinks are unavailable on this platform: {ex.Message}");
        }
    }

    [Fact]
    public async Task Write_ThroughASymlinkedDirectory_ThrowsSecurityException()
    {
        CreateDirectoryLink(Path.Combine(_root, "up_cache"), _outside);

        using var provider = Build();
        var folder = Resolve(provider);

        using var payload = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<SecurityException>(
            async () => await folder.WriteAsync(payload, Path.Combine("up_cache", "payload.bin"), TestTemp.Token));

        // The write was rejected, not redirected: nothing landed in the link's target.
        Assert.False(File.Exists(Path.Combine(_outside, "payload.bin")));
    }

    [Fact]
    public async Task Write_OverASymlinkedFile_ThrowsSecurityException()
    {
        var target = Path.Combine(_outside, "secret.bin");
        await File.WriteAllBytesAsync(target, [9, 9, 9], TestTemp.Token);
        CreateFileLink(Path.Combine(_root, "payload.bin"), target);

        using var provider = Build();
        var folder = Resolve(provider);

        using var payload = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<SecurityException>(
            async () => await folder.WriteAsync(payload, "payload.bin", TestTemp.Token));

        // Overwriting through the link would have truncated the target — it is untouched.
        Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(target, TestTemp.Token));
    }

    [Fact]
    public void CreateSubfolder_ThroughASymlinkedDirectory_ThrowsSecurityException()
    {
        CreateDirectoryLink(Path.Combine(_root, "up_cache"), _outside);

        using var provider = Build();
        var folder = Resolve(provider);

        Assert.Throws<SecurityException>(
            () => folder.CreateSubfolder(Path.Combine("up_cache", "inner")));

        // CreateDirectory must not have built the tree inside the link's target.
        Assert.False(Directory.Exists(Path.Combine(_outside, "inner")));
    }

    [Fact]
    public async Task Cleanup_DoesNotDescendIntoOrDeleteASymlinkedFolder()
    {
        // An outside tree holding an *expired* folder: if cleanup descended through the link it
        // would select `up_link/old` and delete it — in the target's tree, outside the root.
        var victim = Path.Combine(_outside, "old");
        Directory.CreateDirectory(victim);
        await File.WriteAllTextAsync(Path.Combine(victim, "keep.bin"), "keep", TestTemp.Token);
        TestTemp.MakeExpired(victim);

        var link = Path.Combine(_root, "up_link");
        CreateDirectoryLink(link, _outside);

        // A real expired folder in the root proves the pass still did its normal job.
        var real = Path.Combine(_root, "up_real");
        Directory.CreateDirectory(real);
        TestTemp.MakeExpired(real);

        using var provider = Build();
        var folder = Resolve(provider);

        var deleted = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, deleted); // only up_real
        Assert.False(Directory.Exists(real));

        // The outside tree and the link itself were left alone.
        Assert.True(File.Exists(Path.Combine(victim, "keep.bin")));
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public async Task VolumeScan_DoesNotFollowSymlinks()
    {
        // 5000 bytes outside the root, reachable only through links.
        await File.WriteAllBytesAsync(Path.Combine(_outside, "big.bin"), new byte[5000], TestTemp.Token);

        // A top-level link to the outside tree…
        CreateDirectoryLink(Path.Combine(_root, "up_link"), _outside);

        // …a nested link inside a real folder…
        var real = Path.Combine(_root, "up_data");
        Directory.CreateDirectory(real);
        CreateDirectoryLink(Path.Combine(real, "link"), _outside);

        // …and a link cycle (a link back to its own ancestor): none may be walked.
        CreateDirectoryLink(Path.Combine(real, "loop"), real);

        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 100;
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = Resolve(provider);

        // A real 200-byte payload is over the limit; the link targets are not our bytes.
        await File.WriteAllBytesAsync(Path.Combine(real, "payload.bin"), new byte[200], TestTemp.Token);

        await folder.CleanupExpiredAsync(TestTemp.Token);

        TempFolderVolumeArgs[] fired;
        lock (events) fired = events.ToArray();

        var single = Assert.Single(fired);
        Assert.True(single.Exceeded);
        Assert.Equal(200, single.TotalSize);
    }
}
