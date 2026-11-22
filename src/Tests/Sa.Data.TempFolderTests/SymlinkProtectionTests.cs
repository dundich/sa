using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;
using System.Security;

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

    [Fact]
    public async Task Write_ThroughASymlinkedDirectory_ThrowsSecurityException()
    {
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_cache"), _outside);

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
        TestTemp.CreateFileLink(Path.Combine(_root, "payload.bin"), target);

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
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_cache"), _outside);

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
        TestTemp.CreateDirectoryLink(link, _outside);

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
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_link"), _outside);

        // …a nested link inside a real folder…
        var real = Path.Combine(_root, "up_data");
        Directory.CreateDirectory(real);
        TestTemp.CreateDirectoryLink(Path.Combine(real, "link"), _outside);

        // …and a link cycle (a link back to its own ancestor): none may be walked.
        TestTemp.CreateDirectoryLink(Path.Combine(real, "loop"), real);

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

    [Fact]
    public async Task Delete_ThroughASymlinkedDirectory_ThrowsAndLeavesTheTargetIntact()
    {
        // A directory link planted inside the root: resolving the path is textual, so without the
        // component check File.Delete would unlink the target's file, outside the root.
        var target = Path.Combine(_outside, "keep.bin");
        await File.WriteAllBytesAsync(target, [7, 7, 7], TestTemp.Token);
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_cache"), _outside);

        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<SecurityException>(
            () => folder.DeleteFileAsync(Path.Combine("up_cache", "keep.bin"), TestTemp.Token));

        Assert.Equal(new byte[] { 7, 7, 7 }, await File.ReadAllBytesAsync(target, TestTemp.Token));
    }

    [Fact]
    public async Task Read_ThroughASymlinkedDirectory_Throws()
    {
        await File.WriteAllBytesAsync(Path.Combine(_outside, "secret.bin"), [1, 2, 3], TestTemp.Token);
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_cache"), _outside);

        using var provider = Build();
        var folder = Resolve(provider);

        var invoked = false;

        await Assert.ThrowsAsync<SecurityException>(
            () => folder.ReadAsync(
                Path.Combine("up_cache", "secret.bin"),
                (_, _) =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                },
                TestTemp.Token));

        Assert.False(invoked);
    }

    [Fact]
    public async Task Enumerate_DoesNotFollowASymlinkedFolder()
    {
        // The link's tree is not this instance's: a recursive walk must neither report its files
        // (paths outside the root) nor spin on a link cycle.
        await File.WriteAllBytesAsync(Path.Combine(_outside, "outside.txt"), [1], TestTemp.Token);
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_link"), _outside);

        var real = Path.Combine(_root, "up_data");
        Directory.CreateDirectory(real);
        await File.WriteAllTextAsync(Path.Combine(real, "inside.txt"), "x", TestTemp.Token);

        using var provider = Build();
        var folder = Resolve(provider);

        var found = new List<string>();
        await foreach (var file in folder.EnumerateFilesAsync(
            "*.txt", recursive: true, cancellationToken: TestTemp.Token))
        {
            found.Add(file);
        }

        var single = Assert.Single(found);
        Assert.Equal(Path.Combine(real, "inside.txt"), single);
    }

    [Fact]
    public async Task Enumerate_LinkedSubfolder_IsRejected()
    {
        // The link is the *search root* here, not an entry below it: a reparse-point filter on
        // the entries cannot help, and following it would report files that live outside the root
        // (under textually-inside paths that ReadAsync then refuses).
        await File.WriteAllBytesAsync(Path.Combine(_outside, "outside.txt"), [1], TestTemp.Token);
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_link"), _outside);

        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<SecurityException>(async () =>
        {
            await foreach (var _ in folder.EnumerateFilesAsync(
                "*.txt", "up_link", cancellationToken: TestTemp.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Enumerate_LinkedSubfolderRecursive_IsRejected()
    {
        await File.WriteAllBytesAsync(Path.Combine(_outside, "outside.txt"), [1], TestTemp.Token);
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_link"), _outside);

        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<SecurityException>(async () =>
        {
            await foreach (var _ in folder.EnumerateFilesAsync(
                "*.txt", "up_link", recursive: true, TestTemp.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Enumerate_DanglingLinkedSubfolder_IsRejected()
    {
        // The target does not exist, so the folder is not "there" for a filesystem probe — but it
        // is still a link, and silently yielding nothing would hide the escape instead of refusing
        // it. The guard must run before the existence check.
        var missing = Path.Combine(_outside, "gone");
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_dangling"), missing);

        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<SecurityException>(async () =>
        {
            await foreach (var _ in folder.EnumerateFilesAsync(
                "*.txt", "up_dangling", cancellationToken: TestTemp.Token))
            {
            }
        });
    }

    [Fact]
    public void CreateSubfolder_ThroughADanglingLink_ThrowsAndCreatesNothing()
    {
        // The link has no target yet: a "create the parents as needed" implementation would build
        // the whole tree outside the root on the way to the target.
        var target = Path.Combine(_outside, "not-there");
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_cache"), target);

        using var provider = Build();
        var folder = Resolve(provider);

        Assert.Throws<SecurityException>(
            () => folder.CreateSubfolder(Path.Combine("up_cache", "inner")));

        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(target, "inner")));
    }

    [Fact]
    public async Task Write_ThroughADanglingLink_ThrowsAndCreatesNothing()
    {
        var target = Path.Combine(_outside, "not-there");
        TestTemp.CreateDirectoryLink(Path.Combine(_root, "up_cache"), target);

        using var provider = Build();
        var folder = Resolve(provider);

        using var payload = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<SecurityException>(
            async () => await folder.WriteAsync(
                payload, Path.Combine("up_cache", "payload.bin"), TestTemp.Token));

        Assert.False(Directory.Exists(target));
    }
}
