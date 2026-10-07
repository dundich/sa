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
            .AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x =>
            {
                x.BasePath = $"failover_{Path.GetRandomFileName()}";
                x.Basket = "shared";
            })))
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
    public void HybridFileStorage_NoAvailableStorage_ThrowsOnResolve()
    {
        // Resolving used to succeed with an empty container, and the first upload then failed with
        // HybridFileStorageNoAvailableException — a message pointing at the call site rather than
        // at the missing registration. A missing provider is a startup mistake, so it now fails
        // where the mistake is.
        var services = new ServiceCollection()
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHybridFileStorage>());

        Assert.Contains("No IFileStorage provider", ex.Message, StringComparison.Ordinal);
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
            .AddSaFileSystemFileStorage(o => o.Options(ob => ob.Configure(x => x.BasePath = $"download_{Path.GetRandomFileName()}")))
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload a file
        var input = new UploadFileInput { FileName = "downloadable.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("share", input, stream, _cts.Token);

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

    [Fact]
    public async Task HybridFileStorage_DownloadAsync_ContinuesAfterNotFoundWithinTheSameType()
    {
        // Arrange — two backends of one type in one basket; a per-instance interceptor pushes
        // the upload into the second one only, so the first answers "not found" on download.
        var first = new InMemoryFileStorage(new InMemoryFileStorageOptions("shared"));
        var second = new InMemoryFileStorage(new InMemoryFileStorageOptions("shared"));

        var services = new ServiceCollection()
            .AddSaHybridFileStorage(b => b
                .ConfigureStorage((_, c) => c.AddStorage(first).AddStorage(second))
                .ConfigureInterceptors((_, i) => i.AddUploadInterceptor(new BlockInstanceUploadInterceptor(first))));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        var input = new UploadFileInput { FileName = "second-only.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("shared", input, stream, _cts.Token);

        // Act — the first storage returns false; the probing must continue inside "mem"
        // instead of handing the miss back to the caller.
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

    [Fact]
    public async Task HybridFileStorage_DeleteAsync_ContinuesAfterNotFoundWithinTheSameType()
    {
        // Arrange — the file lives only in the second storage of the type (see download above).
        var first = new InMemoryFileStorage(new InMemoryFileStorageOptions("shared"));
        var second = new InMemoryFileStorage(new InMemoryFileStorageOptions("shared"));

        var services = new ServiceCollection()
            .AddSaHybridFileStorage(b => b
                .ConfigureStorage((_, c) => c.AddStorage(first).AddStorage(second))
                .ConfigureInterceptors((_, i) => i.AddUploadInterceptor(new BlockInstanceUploadInterceptor(first))));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        var input = new UploadFileInput { FileName = "second-only.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("shared", input, stream, _cts.Token);

        // Act — the first storage has no such file: false must not end the delete chain.
        var deleted = await storage.DeleteAsync(result.FileId, _cts.Token);

        // Assert
        Assert.True(deleted);
        Assert.Null(await second.GetMetadataAsync(result.FileId, _cts.Token));
    }

    [Fact]
    public async Task HybridFileStorage_GetMetadataAsync_ContinuesAfterNotFoundWithinTheSameType()
    {
        // Arrange — the file lives only in the second storage of the type.
        var first = new InMemoryFileStorage(new InMemoryFileStorageOptions("shared"));
        var second = new InMemoryFileStorage(new InMemoryFileStorageOptions("shared"));

        var services = new ServiceCollection()
            .AddSaHybridFileStorage(b => b
                .ConfigureStorage((_, c) => c.AddStorage(first).AddStorage(second))
                .ConfigureInterceptors((_, i) => i.AddUploadInterceptor(new BlockInstanceUploadInterceptor(first))));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        var input = new UploadFileInput { FileName = "second-only.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("shared", input, stream, _cts.Token);

        // Act
        var metadata = await storage.GetMetadataAsync(result.FileId, _cts.Token);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("second-only.txt", metadata.FileName);
        Assert.Equal(InMemoryFileStorage.DefaultStorageType, metadata.StorageType);
    }

    [Fact]
    public async Task HybridFileStorage_DownloadAsync_DoesNotProbeADifferentTypeAfterNotFound()
    {
        // Arrange — a storage whose type shares the file ID prefix ("m") also recognizes the
        // file ID. After a miss in "mem" the chain must stop at the type change instead of
        // letting the other type answer.
        var mem = new InMemoryFileStorage(new InMemoryFileStorageOptions("share"));
        var other = new FakeFileStorage("share", "m", hasFileOnDownload: true);

        var services = new ServiceCollection()
            .AddSaHybridFileStorage(b => b.ConfigureStorage((_, c) => c.AddStorage(mem).AddStorage(other)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act
        var downloaded = await storage.DownloadAsync(
            "mem://share/1/missing.txt",
            static (_, _) => Task.CompletedTask,
            _cts.Token);

        // Assert
        Assert.False(downloaded);
        Assert.Equal(0, other.DownloadCount);
    }

    [Fact]
    public async Task HybridFileStorage_UploadAsync_UsesTheFirstStorageOfTheChain()
    {
        // Arrange — nothing fails: the file must stay on the first storage of the chain
        // rather than being probed or written further down it.
        var second = new FakeFileStorage("share", "fake");

        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("share"))
            .AddSaHybridFileStorage(b => b.ConfigureStorage((_, c) => c.AddStorage(second)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync(
            "share",
            new UploadFileInput { FileName = "first.txt", TenantId = 1 },
            stream,
            _cts.Token);

        // Assert
        Assert.Equal(InMemoryFileStorage.DefaultStorageType, result.StorageType);
        Assert.Equal(0, second.UploadCount);
    }

    [Fact]
    public async Task HybridFileStorage_UploadAsync_ExceptionFailsOverToTheNextType()
    {
        // Arrange — the first storage throws (an empty file name is rejected by the in-memory
        // provider); the exception must fail over to the next storage whatever its type.
        var second = new FakeFileStorage("share", "fake");

        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("share"))
            .AddSaHybridFileStorage(b => b.ConfigureStorage((_, c) => c.AddStorage(second)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync(
            "share",
            new UploadFileInput { FileName = string.Empty, TenantId = 1 },
            stream,
            _cts.Token);

        // Assert
        Assert.Equal(second.StorageType, result.StorageType);
        Assert.Equal(1, second.UploadCount);
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

// Blocks uploads to one specific storage instance — lets a test place the file in the second
// backend of a chain even when both backends are of the same type.
internal sealed class BlockInstanceUploadInterceptor(IFileStorage blocked) : IUploadInterceptor
{
    public ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken cancellationToken)
        => ValueTask.FromResult(!ReferenceEquals(storage, blocked));

    public ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
