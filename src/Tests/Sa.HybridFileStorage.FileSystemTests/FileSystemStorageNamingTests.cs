using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// The configured <see cref="FileSystemStorageOptions.StorageType"/> and
/// <see cref="FileSystemStorageOptions.Basket"/> are not just validated values: they shape the
/// scheme of every File ID and the on-disk directory the files land in. Covered end to end, on a
/// non-default pair, because the other suites all run on the defaults.
/// </summary>
public sealed class FileSystemStorageNamingTests : IAsyncLifetime
{
    private const string Name = "custom-naming";
    private const string CustomBasket = "documents";
    private const string CustomType = "fsx";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"fs_naming_{Path.GetRandomFileName()}");

    private ServiceProvider _provider = default!;

    public ValueTask InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddFsStorage(_root, Name, b => b.Options(ob => ob.Configure(o =>
            {
                o.Basket = CustomBasket;
                o.StorageType = CustomType;
            })))
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
    public async Task Upload_UsesTheConfiguredSchemeAndBasket_AndLandsUnderThem()
    {
        using var content = new MemoryStream("payload"u8.ToArray());

        var result = await Storage.UploadAsync(
            new UploadFileInput { FileName = "photo.jpg", TenantId = 5 },
            content,
            Token);

        Assert.Equal("fsx://documents/5/photo.jpg", result.FileId);
        Assert.Equal(CustomType, result.StorageType);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), CustomBasket, "5", "photo.jpg"),
            result.AbsoluteUrl);
        Assert.True(File.Exists(result.AbsoluteUrl));
    }

    [Fact]
    public void CanProcess_FollowsTheConfiguredSchemeAndBasket()
    {
        Assert.True(Storage.CanProcess("fsx://documents/5/photo.jpg"));

        // The default scheme and the shared default basket belong to other registrations.
        Assert.False(Storage.CanProcess("fs://documents/5/photo.jpg"));
        Assert.False(Storage.CanProcess("fsx://share/5/photo.jpg"));
    }

    [Fact]
    public async Task Download_RoundTripsUnderTheCustomBasket()
    {
        var expected = "custom"u8.ToArray();

        var result = await Storage.UploadAsync(
            new UploadFileInput { FileName = "roundtrip.bin", TenantId = 7 },
            new MemoryStream(expected),
            Token);

        using var target = new MemoryStream();
        Assert.True(await Storage.DownloadAsync(
            result.FileId,
            async (stream, ct) => await stream.CopyToAsync(target, ct),
            Token));

        Assert.Equal(expected, target.ToArray());
    }

    [Fact]
    public async Task GetMetadata_ReportsTheConfiguredBasketAndType()
    {
        var result = await Storage.UploadAsync(
            new UploadFileInput { FileName = "meta.txt", TenantId = 9 },
            new MemoryStream("x"u8.ToArray()),
            Token);

        var metadata = await Storage.GetMetadataAsync(result.FileId, Token);

        Assert.NotNull(metadata);
        Assert.Equal(CustomBasket, metadata.Basket);
        Assert.Equal(CustomType, metadata.StorageType);
        Assert.Equal(9, metadata.TenantId);
        Assert.Equal("meta.txt", metadata.FileName);
    }
}
