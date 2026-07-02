using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorageTests;

public sealed class FailoverTests : IAsyncLifetime
{
    private readonly CancellationTokenSource _cts = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task HybridFileStorage_AllStoragesFail_ThrowsWritableException()
    {
        // Arrange — two read-only storages, all operations should fail
        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions(string.Empty, IsReadOnly: true))
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions(string.Empty, IsReadOnly: true))
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act & Assert — upload should fail with writable exception (no writable storage)
        await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            storage.UploadAsync(string.Empty, new UploadFileInput { FileName = "fail.bin", TenantId = 1 }, FixtureHelper.GetByteStream(), _cts.Token));
    }

    [Fact]
    public async Task HybridFileStorage_FailoverToSecondStorage_Succeeds()
    {
        // Arrange — first storage blocks upload via interceptor, second accepts
        var blockedCount = 0;
        var interceptor = new CountingBlockInterceptor(() => { blockedCount++; });

        var services = new ServiceCollection()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = $"failover_{Path.GetRandomFileName()}", Basket = "shared" })
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("shared"))
            .AddSaHybridFileStorage(b => b.ConfigureInterceptors((sp, c) => c.AddUploadInterceptor(interceptor)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act — upload blocked on filesystem by interceptor, should succeed on InMemory
        var input = new UploadFileInput { FileName = "failover.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("shared", input, stream, _cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(InMemoryFileStorage.DefaultStorageType, result.StorageType);
        Assert.Equal(1, blockedCount);
    }

    [Fact]
    public async Task HybridFileStorage_WritableException_WhenAllReadOnly()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSaHybridFileStorage(b => b.ConfigureStorage((_, c) =>
                c.AddStorage(new InMemoryFileStorage(new InMemoryFileStorageOptions(string.Empty, IsReadOnly: true)))));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            storage.UploadAsync(string.Empty, new UploadFileInput { FileName = "ro.bin", TenantId = 1 }, FixtureHelper.GetByteStream(), _cts.Token));

        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task HybridFileStorage_NoAvailableStorage_ThrowsNoAvailableException()
    {
        // Arrange — empty service collection, no storages registered
        var services = new ServiceCollection()
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act & Assert
        await Assert.ThrowsAsync<HybridFileStorageNoAvailableException>(() =>
            storage.UploadAsync(string.Empty, new UploadFileInput { FileName = "nope.bin", TenantId = 1 }, FixtureHelper.GetByteStream(), _cts.Token));
    }

    [Fact]
    public async Task HybridFileStorage_GetMetadataAsync_MultipleProviders_ReturnsFirstMatch()
    {
        // Arrange — register two InMemory storages with different baskets
        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("basket-a"))
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("basket-b"))
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload to first available storage
        var input = new UploadFileInput { FileName = "meta.txt", TenantId = 42 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("basket-a", input, stream, _cts.Token);

        // Act — GetMetadata should parse correctly regardless of which storage handled it
        var metadata = await storage.GetMetadataAsync(result.FileId, _cts.Token);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal(42, metadata.TenantId);
        Assert.Equal("meta.txt", metadata.FileName);
        Assert.Equal(InMemoryFileStorage.DefaultStorageType, metadata.StorageType);
    }

    [Fact]
    public async Task HybridFileStorage_DownloadAsync_FromCorrectStorage_Succeeds()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = $"download_{Path.GetRandomFileName()}" })
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload a file
        var input = new UploadFileInput { FileName = "downloadable.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync(string.Empty, input, stream, _cts.Token);

        // Act — download should find and retrieve from the correct storage
        byte[]? downloadedData = null;
        var downloaded = await storage.DownloadAsync(
            result.FileId,
            async (s, ct) => downloadedData = await s.ReadAllBytesAsync(ct),
            _cts.Token);

        // Assert
        Assert.True(downloaded);
        Assert.NotNull(downloadedData);
        Assert.NotEmpty(downloadedData);
    }
}

// Helper extension for reading all bytes
internal static class StreamExtensions
{
    internal static async Task<byte[]> ReadAllBytesAsync(this Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}

// Helper interceptor for testing
internal sealed class CountingBlockInterceptor(Action onBlock) : IUploadInterceptor
{
    public ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken cancellationToken)
    {
        // Block uploads to filesystem storage
        if (storage.StorageType == "fs")
        {
            onBlock();
            return ValueTask.FromResult(false);
        }
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
