using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// What this storage claims to own. A File ID is caller input, and <c>CanProcess</c> is what the
/// hybrid chain uses to pick a candidate and what gates download/delete here — so a foreign scheme,
/// a sibling basket that only shares the name prefix, and an ID the OS cannot resolve must all read
/// as "not mine" instead of being served or throwing.
/// </summary>
public sealed class FileSystemStoragePathTests : IAsyncLifetime
{
    private const string Name = "path-ownership";
    private const string Basket = FileSystemStorageOptions.DefaultBasket;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"fs_paths_{Path.GetRandomFileName()}");

    private ServiceProvider _provider = default!;

    public ValueTask InitializeAsync()
    {
        _provider = new ServiceCollection().AddFsStorage(_root, Name).BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _provider.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private IFileStorage Storage => _provider.GetRequiredService<IFileStorage>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pg://share/1/x.txt")]
    [InlineData("fsx://share/1/x.txt")]
    [InlineData("fs://share2/1/x.txt")]              // sibling basket that shares the prefix
    [InlineData("fs://share-evil/1/x.txt")]          // ditto, with a dash
    [InlineData("fs://share/1/../../../etc/passwd")] // resolves outside the basket
    public void CanProcess_RejectsIdsThisStorageDoesNotOwn(string fileId)
        => Assert.False(Storage.CanProcess(fileId));

    [Fact]
    public void CanProcess_RejectsNullAndBlank()
    {
        Assert.False(Storage.CanProcess(null!));
        Assert.False(Storage.CanProcess(string.Empty));
    }

    [Fact]
    public void CanProcess_ReturnsFalse_ForAnIdTheOsCannotResolve()
    {
        // A NUL character makes Path.GetFullPath throw. The hybrid chain probes every storage with
        // CanProcess before any failover handling, so this must be "not mine", not an exception.
        Assert.False(Storage.CanProcess("fs://share/1/a\0b.txt"));
    }

    [Fact]
    public void CanProcess_AcceptsItsOwnBasket()
        => Assert.True(Storage.CanProcess($"fs://{Basket}/1/x.txt"));

    [Fact]
    public async Task Download_OfASiblingBasketId_DoesNotServeTheFile()
    {
        // The file really exists — in the sibling basket — so this is about ownership, not absence.
        var siblingDir = Path.Combine(_root, "share2", "1");
        Directory.CreateDirectory(siblingDir);
        await File.WriteAllTextAsync(Path.Combine(siblingDir, "x.txt"), "sibling", Token);

        var downloaded = await Storage.DownloadAsync(
            "fs://share2/1/x.txt",
            static (_, _) => Task.CompletedTask,
            Token);

        Assert.False(downloaded);
    }

    [Fact]
    public async Task Upload_NestedName_KeepsItsDirectories_AndProducesAPortableId()
    {
        using var content = new MemoryStream("payload"u8.ToArray());

        var result = await Storage.UploadAsync(
            new UploadFileInput { FileName = "sub/dir/file.txt", TenantId = 7 },
            content,
            Token);

        // Forward slashes on every OS: the sanitizer emits the platform separator, the ID does not.
        Assert.Equal($"fs://{Basket}/7/sub/dir/file.txt", result.FileId);

        // The on-disk layout mirrors the ID, under the temp folder's resolved root.
        var root = _provider.GetRequiredKeyedService<ITempFolder>(Name).RootPath;
        Assert.Equal(
            Path.Combine(root, Basket, "7", "sub", "dir", "file.txt"),
            result.AbsoluteUrl);
    }

    [Fact]
    public async Task Upload_NestedName_RoundTrips()
    {
        var expected = "roundtrip"u8.ToArray();
        using var content = new MemoryStream(expected);

        var result = await Storage.UploadAsync(
            new UploadFileInput { FileName = "/api/files/var/www/report.bin", TenantId = 3 },
            content,
            Token);

        using var target = new MemoryStream();
        var downloaded = await Storage.DownloadAsync(
            result.FileId,
            async (stream, ct) => await stream.CopyToAsync(target, ct),
            Token);

        Assert.True(downloaded);
        Assert.Equal(expected, target.ToArray());
    }
}
