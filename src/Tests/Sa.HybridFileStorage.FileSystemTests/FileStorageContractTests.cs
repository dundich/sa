using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// The contract the storage exposes to its callers — upload, download, delete, metadata — now that
/// file I/O and its transient-failure retries belong to
/// <see cref="Sa.Data.TempFolder.TempFolder"/>. The deterministic retry mechanics are covered
/// there; this class pins the storage's own edges: successful operations, "no file" as
/// <c>false</c>/<c>null</c>, argument guards, cancellation, and the undeletable-file branch.
/// </summary>
public sealed class FileStorageContractTests : IAsyncLifetime
{
    private readonly string _testDir =
        Path.Combine(Path.GetTempPath(), $"retry_{Path.GetRandomFileName()}");
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
            .AddFsStorage(_testDir);

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
            .AddFsStorage(_testDir);

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
            .AddFsStorage(_testDir);

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
            .AddFsStorage(_testDir);

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
            .AddFsStorage(_testDir);

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
        // The configured basket, not the one re-parsed out of the ID.
        Assert.Equal(storage.Basket, metadata.Basket);
    }

    [Fact]
    public async Task GetMetadataAsync_NumericDirectoryInTheName_IsNotReadAsATimestamp()
    {
        // A filesystem name may contain a directory named with a number. The parser must read the
        // whole path after the tenant as the name, not the Postgres `tenant/timestamp/name` shape.
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        var uploadResult = await storage.UploadAsync(
            new UploadFileInput { FileName = "2024/report.pdf", TenantId = 42 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        // Act
        var metadata = await storage.GetMetadataAsync(uploadResult.FileId, _cts.Token);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("2024/report.pdf", metadata.FileName);
        Assert.Equal(42, metadata.TenantId);
    }

    [Fact]
    public async Task DownloadAsync_NullArguments_Throw()
    {
        using var provider = BuildStorage();
        var storage = provider.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.DownloadAsync(null!, static (_, _) => Task.CompletedTask, _cts.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.DownloadAsync("fs://share/1/x.txt", null!, _cts.Token));
    }

    [Fact]
    public async Task DeleteAsync_NullFileId_Throws()
    {
        using var provider = BuildStorage();
        var storage = provider.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.DeleteAsync(null!, _cts.Token));
    }

    [Fact]
    public async Task DownloadAsync_PreCancelledToken_Throws()
    {
        // The token is checked before the activity gate, so an operation nobody waits for any more
        // never starts — and never cancels a running cleanup pass on its way out.
        using var provider = BuildStorage();
        var storage = provider.GetRequiredService<IFileStorage>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            storage.DownloadAsync("fs://share/1/x.txt", static (_, _) => Task.CompletedTask, cts.Token));
    }

    [Fact]
    public async Task DeleteAsync_PreCancelledToken_Throws()
    {
        using var provider = BuildStorage();
        var storage = provider.GetRequiredService<IFileStorage>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.DeleteAsync("fs://share/1/x.txt", cts.Token));
    }

    [Fact]
    public async Task DeleteAsync_UndeletableFile_ReturnsFalse()
    {
        // The temp folder retries a transient I/O failure and rethrows it; the storage maps the
        // survivor to "not deleted", which is the contract this provider had when it deleted files
        // itself. Made deterministic with a real permission denial rather than a mock.
        using var provider = BuildStorage();
        var storage = provider.GetRequiredService<IFileStorage>();

        var upload = await storage.UploadAsync(
            new UploadFileInput { FileName = "locked.txt", TenantId = 1 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        var parent = Path.GetDirectoryName(upload.AbsoluteUrl)!;

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(upload.AbsoluteUrl, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            var deleted = await storage.DeleteAsync(upload.FileId, _cts.Token);

            if (!File.Exists(upload.AbsoluteUrl))
            {
                // A superuser or a filesystem with permission checking off cannot produce the
                // failure this branch exists for.
                Assert.Skip("the platform did not enforce the write permission");
            }

            Assert.False(deleted);
            Assert.True(File.Exists(upload.AbsoluteUrl));
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(upload.AbsoluteUrl, FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(
                    parent,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public async Task GetMetadataAsync_NullFileId_ReturnsNull()
    {
        // The predicate swallows the missing id: metadata is a lookup, not a command, and a null
        // ID simply belongs to no storage.
        using var provider = BuildStorage();
        var storage = provider.GetRequiredService<IFileStorage>();

        Assert.Null(await storage.GetMetadataAsync(null!, _cts.Token));
    }

    [Fact]
    public async Task GetMetadataAsync_MalformedId_ReturnsNull()
    {
        using var provider = BuildStorage();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Inside the basket, but not a file ID of this layout: the tenant is not a number.
        Assert.Null(await storage.GetMetadataAsync("fs://share/abc/x.txt", _cts.Token));
    }

    [Fact]
    public async Task GetMetadataAsync_FileMissingOnDisk_StillReportsMetadata()
    {
        // Metadata is parsed from the ID, never probed on disk — a listing must not turn into one
        // stat per row.
        using var provider = BuildStorage();
        var storage = provider.GetRequiredService<IFileStorage>();

        var metadata = await storage.GetMetadataAsync("fs://share/1/never-uploaded.txt", _cts.Token);

        Assert.NotNull(metadata);
        Assert.Equal("never-uploaded.txt", metadata.FileName);
        Assert.Equal(1, metadata.TenantId);
    }

    private ServiceProvider BuildStorage()
    {
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        return services.BuildServiceProvider();
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
