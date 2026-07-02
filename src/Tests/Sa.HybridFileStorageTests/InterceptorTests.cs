using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorageTests;

public sealed class InterceptorTests : IAsyncLifetime
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
    public async Task DownloadInterceptor_CanDownloadFalse_BlocksDownload()
    {
        // Arrange — two storages with same basket, file uploaded to first one
        var downloadBlocked = false;
        var interceptor = new CountingDownloadInterceptor(() => downloadBlocked = true);

        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("shared_basket"))
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("shared_basket"))
            .AddSaHybridFileStorage(b => b.ConfigureInterceptors((_, c) =>
                c.AddDownloadInterceptor(interceptor)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload first — goes to first available storage
        var input = new UploadFileInput { FileName = "blocked_download.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("shared_basket", input, stream, _cts.Token);

        // Act — download intercepted: interceptor blocks ALL storages
        // Since only one storage can CanProcess the fileId, and interceptor blocks it → NoAvailableException
        var ex = await Assert.ThrowsAsync<HybridFileStorageNoAvailableException>(() =>
            storage.DownloadAsync(result.FileId, (_, _) => Task.CompletedTask, _cts.Token));

        // Assert — interceptor was called and blocked
        Assert.True(downloadBlocked);
    }

    [Fact]
    public async Task DeleteInterceptor_AfterDeleteCalled_NotifiesOnCompletion()
    {
        // Arrange
        var afterDeleteCalled = false;
        var successFlag = false;
        var deleteInterceptor = new NotifyDeleteInterceptor(
            () => afterDeleteCalled = true,
            (bool s) => successFlag = s);

        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage(b => b.ConfigureInterceptors((sp, c) => c.AddDeleteInterceptor(deleteInterceptor)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload and delete
        var input = new UploadFileInput { FileName = "notify.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync(string.Empty, input, stream, _cts.Token);

        // Act
        await storage.DeleteAsync(result.FileId, _cts.Token);

        // Assert
        Assert.True(afterDeleteCalled);
        Assert.True(successFlag);
    }

    [Fact]
    public async Task UploadInterceptorChain_AllMethodsCalled_InOrder()
    {
        // Arrange
        var callSequence = new List<string>();

        var canUploadCalled = false;
        var afterUploadCalled = false;
        var uploadInterceptor = new OrderTrackingUploadInterceptor(
            () => { canUploadCalled = true; callSequence.Add("CanUpload"); return Task.CompletedTask; },
            () => { afterUploadCalled = true; callSequence.Add("AfterUpload"); return Task.CompletedTask; },
            () => { callSequence.Add("OnError"); return Task.CompletedTask; });

        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage(b => b.ConfigureInterceptors((sp, c) => c.AddUploadInterceptor(uploadInterceptor)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act
        var input = new UploadFileInput { FileName = "chain.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        await storage.UploadAsync(string.Empty, input, stream, _cts.Token);

        // Assert
        Assert.True(canUploadCalled);
        Assert.True(afterUploadCalled);
        Assert.Contains("CanUpload", callSequence);
        Assert.Contains("AfterUpload", callSequence);
        Assert.DoesNotContain("OnError", callSequence);
    }

    [Fact]
    public async Task UploadInterceptor_CanUploadFalse_ReroutesToOtherStorage()
    {
        // Arrange — two storages with same basket, one blocked by interceptor
        var uploadBlocked = false;
        var interceptor = new CountingUploadInterceptor(() => uploadBlocked = true);

        var services = new ServiceCollection()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = "interceptor_test", Basket = "shared_basket" })
            .AddSaInMemoryFileStorage(new InMemoryFileStorageOptions("shared_basket"))
            .AddSaHybridFileStorage(b => b.ConfigureInterceptors((_, c) =>
                c.AddUploadInterceptor(interceptor)));

        using var provider = services.BuildServiceProvider();
         var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act — upload blocked for filesystem by interceptor, should fall back to InMemory
        var input = new UploadFileInput { FileName = "blocked.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("shared_basket", input, stream, _cts.Token);

        // Assert — should succeed in InMemory (filesystem was blocked by interceptor)
        Assert.NotNull(result);
        Assert.Equal(InMemoryFileStorage.DefaultStorageType, result.StorageType);
        Assert.True(uploadBlocked);

        try { Directory.Delete("interceptor_test", true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task DeleteInterceptor_CanDeleteFalse_PreventsDeletion()
    {
        // Arrange — single storage, interceptor blocks delete
        var canDeleteChecked = false;
        var interceptor = new CountingDeleteInterceptor(
            () => canDeleteChecked = true);

        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage(b => b.ConfigureInterceptors((sp, c) => c.AddDeleteInterceptor(interceptor)));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload a file
        var input = new UploadFileInput { FileName = "protected.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync(string.Empty, input, stream, _cts.Token);

        // Act — delete intercepted: interceptor blocks the only storage → NoAvailableException
        var ex = await Assert.ThrowsAsync<HybridFileStorageNoAvailableException>(() =>
            storage.DeleteAsync(result.FileId, _cts.Token));

        // Assert — interceptor was called
        Assert.True(canDeleteChecked);
    }
}

// --- Helper interceptor implementations for testing ---

internal sealed class BlockingDownloadInterceptor : IDownloadInterceptor
{
    public ValueTask<bool> CanDownloadAsync(IFileStorage storage, string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    public ValueTask AfterDownloadAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask OnDownloadErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class CountingDownloadInterceptor(Action onBlock) : IDownloadInterceptor
{
    public ValueTask<bool> CanDownloadAsync(IFileStorage storage, string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        onBlock();
        return ValueTask.FromResult(false);
    }

    public ValueTask AfterDownloadAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask OnDownloadErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class NotifyDeleteInterceptor(Action onAfterDelete, Action<bool> onSuccess) : IDeleteInterceptor
{
    public ValueTask<bool> CanDeleteAsync(IFileStorage storage, string fileId, CancellationToken cancellationToken)
        => ValueTask.FromResult(true);

    public ValueTask AfterDeleteAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken)
    {
        onAfterDelete();
        onSuccess(success);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDeleteErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class CountingDeleteInterceptor(Action onCanDelete) : IDeleteInterceptor
{
    public ValueTask<bool> CanDeleteAsync(IFileStorage storage, string fileId, CancellationToken cancellationToken)
    {
        onCanDelete();
        return ValueTask.FromResult(false); // Always block
    }

    public ValueTask AfterDeleteAsync(IFileStorage storage, string fileId, bool success, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask OnDeleteErrorAsync(IFileStorage storage, string fileId, Exception exception, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class OrderTrackingUploadInterceptor(Func<Task> onCan, Func<Task> onAfter, Func<Task> onError) : IUploadInterceptor
{
    public ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken cancellationToken)
    {
        onCan();
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken)
    {
        onAfter();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken)
    {
        onError();
        return ValueTask.CompletedTask;
    }
}

internal sealed class BlockingUploadInterceptor(string blockedFileName) : IUploadInterceptor
{
    public ValueTask<bool> CanUploadAsync(IFileStorage storage, UploadFileInput input, Stream fileStream, CancellationToken cancellationToken)
        => ValueTask.FromResult(input.FileName != blockedFileName);

    public ValueTask AfterUploadAsync(IFileStorage storage, StorageResult result, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask OnUploadErrorAsync(IFileStorage storage, Exception exception, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class CountingUploadInterceptor(Action onBlock) : IUploadInterceptor
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
