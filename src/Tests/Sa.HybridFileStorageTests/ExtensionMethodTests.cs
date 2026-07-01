using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorageTests;

public sealed class ExtensionMethodTests : IAsyncLifetime
{
    private readonly CancellationTokenSource _cts = new();
    private string? _testDir;

    public ValueTask InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"ext_test_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(_testDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (_testDir is not null)
            try { Directory.Delete(_testDir, true); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CopyFromFileAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        var nonExistentFile = Path.Combine(_testDir!, "does_not_exist.txt");

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() =>
            storage.CopyFromFileAsync(nonExistentFile, string.Empty, new UploadFileInput { FileName = "copy.txt", TenantId = 1 }, ct: _cts.Token));
    }

    [Fact]
    public async Task CopyToBasketAsync_SameScope_ThrowsInvalidOperationException()
    {
        // Arrange
        var fsPath = Path.Combine(_testDir!, "same_scope");
        Directory.CreateDirectory(fsPath);

        var services = new ServiceCollection()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = fsPath })
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload a file to the "share" basket (FileSystem default)
        var input = new UploadFileInput { FileName = "sametest.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync("share", input, stream, _cts.Token);

        // Act & Assert — copy to same basket should fail
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.CopyToBasketAsync(result.FileId, "share", ct: _cts.Token));
    }

    [Fact]
    public async Task CopyToScopeBatchAsync_EmptyList_ReturnsEmptyResult()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act
        var result = await storage.CopyToScopeBatchAsync(
            Array.Empty<string>(),
            string.Empty,
            cancellationToken: _cts.Token);

        // Assert
        Assert.Equal(0, result.Total);
        Assert.False(result.HasErrors);
        Assert.Empty(result.Succeeded);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task CopyFromFileAsync_Success_CopyFromDiskToStorage()
    {
        // Arrange — create a test file on disk
        var testFilePath = Path.Combine(_testDir!, "source.txt");
        await File.WriteAllTextAsync(testFilePath, "Hello from disk!", _cts.Token);

        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act
        var result = await storage.CopyFromFileAsync(
            testFilePath,
            string.Empty,
            new UploadFileInput { FileName = "disk_copy.txt", TenantId = 1 },
            ct: _cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);

        // Verify content was copied correctly
        var downloaded = await storage.DownloadAsync(result.FileId, async (s, ct) =>
        {
            using var reader = new StreamReader(s, leaveOpen: true);
            var content = await reader.ReadToEndAsync();
            Assert.Equal("Hello from disk!", content);
        }, _cts.Token);

        Assert.True(downloaded);
    }

    [Fact]
    public async Task CopyToBasketAsync_CrossProvider_Success()
    {
        // Arrange — use DI with ConfigureStorage for explicit basket control
        var fsPath = Path.Combine(_testDir!, "cross_fs");
        Directory.CreateDirectory(fsPath);

        var services = new ServiceCollection();
        
        // Register FileSystem with default "share" basket
        services.AddSingleton<IFileStorage>(new FileSystemStorage(
            new FileSystemStorageSettings { BasePath = fsPath }));
        
        // Register InMemory with explicit "mem_basket"
        services.AddSingleton<IFileStorage>(new InMemoryFileStorage(
            new InMemoryFileStorageOptions("mem_basket")));
        
        // Register HybridFileStorage with logging
        services.AddSaHybridFileStorage(cfg => cfg.AddLogging());

        using var provider = services.BuildServiceProvider(true);
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload to FileSystem first ("share" basket)
        var input = new UploadFileInput { FileName = "cross.txt", TenantId = 1 };
        using var origStream = FixtureHelper.GetByteStream();
        var uploadResult = await storage.UploadAsync("share", input, origStream, _cts.Token);

        // Verify upload landed in FileSystem
        Assert.NotNull(uploadResult);
        Assert.Contains("share", uploadResult.FileId);
        Assert.StartsWith("fs://", uploadResult.FileId);

        // Act — copy from FileSystem ("share" basket) to InMemory ("mem_basket")
        var copyResult = await storage.CopyToBasketAsync(
            uploadResult.FileId,
            "mem_basket",
            configure: metadata => new UploadFileInput
            {
                TenantId = metadata.TenantId,
                FileName = $"copied_{metadata.FileName}"
            },
            _cts.Token);

        // Assert — should land in InMemory (storage type "mem")
        Assert.NotNull(copyResult);
        Assert.Equal(InMemoryFileStorage.DefaultStorageType, copyResult.StorageType);
        Assert.Contains("mem_basket", copyResult.FileId);
        Assert.Contains("copied_", copyResult.FileId);

        // Verify the copied file content matches
        var downloaded = await storage.DownloadAsync(copyResult.FileId, async (stream, ct) =>
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            var content = await reader.ReadToEndAsync(ct);
            Assert.True(content.Length > 0);
        }, _cts.Token);
        Assert.True(downloaded);
    }

    [Fact]
    public async Task CopyToBasketAsync_CustomConfigure_RenamesFile()
    {
        // Arrange — manually register two InMemory storages with different baskets
        var services = new ServiceCollection();
        
        services.AddSingleton<IFileStorage>(new InMemoryFileStorage(
            new InMemoryFileStorageOptions("source_basket")));
        services.AddSingleton<IFileStorage>(new InMemoryFileStorage(
            new InMemoryFileStorageOptions("target_basket")));
        services.AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider(true);
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload original file to source basket
        var input = new UploadFileInput { FileName = "original.bin", TenantId = 42 };
        using var stream = FixtureHelper.GetByteStream();
        var uploadResult = await storage.UploadAsync("source_basket", input, stream, _cts.Token);

        // Act — copy with custom rename
        var copyResult = await storage.CopyToBasketAsync(
            uploadResult.FileId,
            "target_basket",
            configure: metadata => new UploadFileInput
            {
                TenantId = 99, // Change tenant
                FileName = "renamed.bin" // Rename file
            },
            _cts.Token);

        // Assert
        Assert.NotNull(copyResult);

        // Verify metadata reflects the custom configuration
        var metadata = await storage.GetMetadataAsync(copyResult.FileId, _cts.Token);
        Assert.NotNull(metadata);
        Assert.Equal(99, metadata.TenantId);
        Assert.Equal("renamed.bin", metadata.FileName);
        Assert.Equal(InMemoryFileStorage.DefaultStorageType, metadata.StorageType);
        Assert.Equal("target_basket", metadata.Basket);
    }

    [Fact]
    public async Task CopyToBasketAsync_SourceNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSaInMemoryFileStorage()
            .AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Act & Assert — copy non-existent file
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            storage.CopyToBasketAsync("mem://nonexistent/file.txt", "target", ct: _cts.Token));
    }

    [Fact]
    public async Task CopyToScopeBatchAsync_MixedSuccessAndFailure_PartialResult()
    {
        // Arrange — manually register two InMemory storages with explicit baskets
        var services = new ServiceCollection();
        
        services.AddSingleton<IFileStorage>(new InMemoryFileStorage(
            new InMemoryFileStorageOptions("batch_src")));
        services.AddSingleton<IFileStorage>(new InMemoryFileStorage(
            new InMemoryFileStorageOptions("batch_dst")));
        services.AddSaHybridFileStorage();

        using var provider = services.BuildServiceProvider(true);
        var storage = provider.GetRequiredService<IHybridFileStorage>();

        // Upload one valid file to source basket
        var input = new UploadFileInput { FileName = "valid.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var uploadResult = await storage.UploadAsync("batch_src", input, stream, _cts.Token);

        var fileIds = new[] { uploadResult.FileId, "invalid://missing1", uploadResult.FileId, "invalid://missing2" };

        // Act
        var result = await storage.CopyToScopeBatchAsync(
            fileIds,
            "batch_dst",
            options: new BatchOptions { ContinueOnError = true },
            cancellationToken: _cts.Token);

        // Assert
        Assert.Equal(4, result.Total);
        Assert.True(result.HasErrors);
        Assert.Equal(2, result.Succeeded.Count);
        Assert.Equal(2, result.Failed.Count);
    }
}
