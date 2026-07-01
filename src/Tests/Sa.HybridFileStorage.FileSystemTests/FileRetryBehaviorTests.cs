using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// Tests that exercise FileRetryHelper behavior through FileSystemStorage operations.
/// FileRetryHelper is used internally by FileSystemStorage for file operations.
/// </summary>
public sealed class FileRetryBehaviorTests : IAsyncLifetime
{
    private readonly string _testDir = $"retry_{Path.GetRandomFileName()}";
    private readonly CancellationTokenSource _cts = new();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_testDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { Directory.Delete(_testDir, true); } catch { /* ignore */ }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task UploadAsync_NormalFile_SucceedsWithoutRetry()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act — normal upload should succeed immediately
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "normal.txt", TenantId = 1 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);
    }

    [Fact]
    public async Task DownloadAsync_NormalFile_ReturnsContent()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        var testData = FixtureHelper.GetByteArray(512);
        using var testStream = new MemoryStream(testData);

        // Upload first
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "downloadable.bin", TenantId = 1 },
            testStream,
            _cts.Token);

        // Act — download and verify content
        byte[]? downloaded = null;
        var success = await storage.DownloadAsync(result.FileId, async (s, ct) =>
        {
            downloaded = await s.ReadAllBytesAsync(ct);
        }, _cts.Token);

        // Assert
        Assert.True(success);
        Assert.NotNull(downloaded);
        Assert.Equal(testData, downloaded);
    }

    [Fact]
    public async Task DeleteAsync_ExistingFile_ReturnsTrue()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Upload a file first
        var uploadResult = await storage.UploadAsync(
            new UploadFileInput { FileName = "deletable.txt", TenantId = 1 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        // Act
        var deleted = await storage.DeleteAsync(uploadResult.FileId, _cts.Token);

        // Assert
        Assert.True(deleted);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act — delete non-existent file
        var deleted = await storage.DeleteAsync("fs://nonexistent-file-id", _cts.Token);

        // Assert
        Assert.False(deleted);
    }

    [Fact]
    public async Task GetMetadataAsync_ValidFileId_ReturnsMetadata()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Upload a file
        var uploadResult = await storage.UploadAsync(
            new UploadFileInput { FileName = "metadata.txt", TenantId = 42 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        // Act
        var metadata = await storage.GetMetadataAsync(uploadResult.FileId, _cts.Token);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal(42, metadata.TenantId);
        Assert.Equal("metadata.txt", metadata.FileName);
        Assert.Equal("fs", metadata.StorageType);
    }
}

// Helper extension
internal static class StreamExtensions
{
    internal static async Task<byte[]> ReadAllBytesAsync(this Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
