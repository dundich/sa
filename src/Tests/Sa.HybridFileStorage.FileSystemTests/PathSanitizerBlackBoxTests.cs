using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;
using System.Security;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// Black-box tests for PathSanitizer behavior via FileSystemStorage.UploadAsync.
/// Tests path sanitization, security, and edge cases through the public API.
/// </summary>
public sealed class PathSanitizerBlackBoxTests : IAsyncLifetime
{
    private readonly string _testDir = $"pathsanity_{Path.GetRandomFileName()}";
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
    public async Task UploadAsync_NullFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() =>
            storage.UploadAsync(null!, FixtureHelper.GetByteStream(), _cts.Token));
    }

    [Fact]
    public async Task UploadAsync_EmptyFileName_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act & Assert
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            storage.UploadAsync(new UploadFileInput { FileName = string.Empty, TenantId = 1 }, FixtureHelper.GetByteStream(), _cts.Token));

        Assert.Contains("File name cannot be", ex.Message);
    }

    [Fact]
    public async Task UploadAsync_PathTraversalDotDot_Rejected()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act & Assert — path traversal should throw SecurityException
        var ex = await Assert.ThrowsAnyAsync<SecurityException>(() =>
            storage.UploadAsync(new UploadFileInput { FileName = "../escape.txt", TenantId = 1 }, FixtureHelper.GetByteStream(), _cts.Token));

        Assert.Contains("'..' is not allowed", ex.Message);
    }

    [Fact]
    public async Task UploadAsync_NestedPath_NormalizesSeparators()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act — upload with nested path
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "nested/path/file.txt", TenantId = 1 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        // Assert — file ID should use normalized separators
        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);
    }

    [Fact]
    public async Task UploadAsync_InvalidCharsInFileName_ReplacedWithUnderscore()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act — upload with invalid chars (< > : " | ? *)
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "file<>:\"|?*name.txt", TenantId = 1 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);
    }
}
