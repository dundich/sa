using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// The observable side of <see cref="FileSystemStorageOptions.IsReadOnly"/>: the flag is not just
/// carried into the storage, it refuses both writes and deletes — and leaves reads alone.
/// </summary>
public sealed class FileSystemStorageReadOnlyTests : IAsyncLifetime
{
    private const string Name = "read-only";
    private const string Basket = FileSystemStorageOptions.DefaultBasket;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"fs_readonly_{Path.GetRandomFileName()}");

    private ServiceProvider _provider = default!;

    public ValueTask InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddFsStorage(_root, Name, b => b.Options(ob => ob.Configure(o => o.IsReadOnly = true)))
            .BuildServiceProvider();
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

    [Fact]
    public async Task Upload_OnAReadOnlyStorage_Throws()
    {
        using var content = new MemoryStream("payload"u8.ToArray());

        await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            Storage.UploadAsync(
                new UploadFileInput { FileName = "blocked.txt", TenantId = 1 },
                content,
                Token));

        Assert.False(Directory.Exists(Path.Combine(_root, Basket)));
    }

    [Fact]
    public async Task Delete_OnAReadOnlyStorage_Throws()
    {
        var fileId = $"fs://{Basket}/1/existing.txt";
        var path = Path.Combine(_root, Basket, "1", "existing.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "keep me", Token);

        await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            Storage.DeleteAsync(fileId, Token));

        Assert.True(File.Exists(path), "a read-only storage must not delete the file");
    }

    [Fact]
    public async Task Download_OnAReadOnlyStorage_ServesTheFile()
    {
        var path = Path.Combine(_root, Basket, "1", "readable.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "read me", Token);

        using var target = new MemoryStream();
        var downloaded = await Storage.DownloadAsync(
            $"fs://{Basket}/1/readable.txt",
            async (stream, ct) => await stream.CopyToAsync(target, ct),
            Token);

        Assert.True(downloaded);
        Assert.Equal("read me"u8.ToArray(), target.ToArray());
    }
}
