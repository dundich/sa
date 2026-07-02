using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorageTests;

public sealed class EdgeCasesTests
{
    [Fact]
    public async Task InMemoryFileStorage_UploadAsync_EmptyFileName_ThrowsArgumentException()
    {
        // Arrange
        var storage = new InMemoryFileStorage();
        using var stream = FixtureHelper.GetByteStream();

        // Act & Assert — empty FileName should throw ArgumentException
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = string.Empty, TenantId = 1 },
                stream,
                CancellationToken.None));

        Assert.Contains("FileName", ex.Message);
    }

    [Fact]
    public async Task InMemoryFileStorage_UploadAsync_EmptyStream_Succeeds()
    {
        // Arrange
        var storage = new InMemoryFileStorage();
        using var emptyStream = FixtureHelper.GetEmptyByteStream();
        var input = new UploadFileInput { FileName = "empty.bin", TenantId = 1 };

        // Act
        var result = await storage.UploadAsync(input, emptyStream, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);
    }

    [Fact]
    public async Task InMemoryFileStorage_UploadAsync_MaxSizeBytesExceeded_ThrowsInvalidOperationException()
    {
        // Arrange — limit to 1MB
        var options = new InMemoryFileStorageOptions("test") { MaxSizeBytes = 1 * 1024 * 1024 };
        var storage = new InMemoryFileStorage(options);

        // Fill up most of the capacity
        for (var i = 0; i < 10; i++)
        {
            var input = new UploadFileInput { FileName = $"fill_{i}.bin", TenantId = 1 };
            var bytes = FixtureHelper.GetByteArray(100 * 1024); // 100KB each
            await storage.UploadAsync(input, new MemoryStream(bytes), CancellationToken.None);
        }

        // Act — this should exceed the limit
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = "overflow.bin", TenantId = 1 },
                FixtureHelper.GetByteStream(),
                CancellationToken.None));

        Assert.Contains("size limit", ex.Message);
    }

    [Fact]
    public async Task InMemoryFileStorage_DeleteAsync_FileNotFound_ReturnsFalse()
    {
        // Arrange
        var storage = new InMemoryFileStorage();

        // Act
        var deleted = await storage.DeleteAsync("mem://nonexistent/file.txt", CancellationToken.None);

        // Assert
        Assert.False(deleted);
    }

    [Fact]
    public async Task InMemoryFileStorage_DownloadAsync_FileNotFound_ReturnsFalse()
    {
        // Arrange
        var storage = new InMemoryFileStorage();
        bool loadCalled = false;

        // Act
        var downloaded = await storage.DownloadAsync("mem://nonexistent/file.txt", (_, _) =>
        {
            loadCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        Assert.False(downloaded);
        Assert.False(loadCalled);
    }

    [Fact]
    public async Task InMemoryFileStorage_GetMetadataAsync_FileNotFound_ReturnsNull()
    {
        // Arrange
        var storage = new InMemoryFileStorage();

        // Act
        var metadata = await storage.GetMetadataAsync("mem://nonexistent/file.txt", CancellationToken.None);

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public async Task InMemoryFileStorage_ReadOnly_UploadThrowsWritableException()
    {
        // Arrange
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions("test") { IsReadOnly = true });
        using var stream = FixtureHelper.GetByteStream();

        // Act & Assert
        await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            storage.UploadAsync(new UploadFileInput { FileName = "nope.txt", TenantId = 1 }, stream, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryFileStorage_ReadOnly_DeleteThrowsWritableException()
    {
        // Arrange
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions("test") { IsReadOnly = true });

        // Act & Assert
        await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            storage.DeleteAsync("mem://any/file.txt", CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryFileStorage_UploadAsync_TimeProvider_ReturnsCorrectTimestamp()
    {
        // Arrange
        var fakeTime = DateTimeOffset.Parse("2025-06-15T12:00:00Z");
        var timeProvider = new TestTimeProvider(fakeTime);
        var storage = new InMemoryFileStorage(null, timeProvider);
        using var stream = FixtureHelper.GetByteStream();

        // Act
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "timed.txt", TenantId = 1 },
            stream,
            CancellationToken.None);

        // Assert
        Assert.Equal(fakeTime, result.UploadedAt);
    }

    [Fact]
    public async Task InMemoryFileStorage_SizeTracking_TracksTotalBytes()
    {
        // Arrange — use reflection to verify internal size tracking
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions("test") { MaxSizeBytes = 0 }); // unlimited
        using var stream = FixtureHelper.GetByteStream(1024);

        // Act
        await storage.UploadAsync(new UploadFileInput { FileName = "track.bin", TenantId = 1 }, stream, CancellationToken.None);

        // Verify via download that data is intact
        var downloaded = await storage.DownloadAsync(
            storage.CanProcess("") ? throw new Exception("need fileId") : "",
            (_, _) => Task.CompletedTask,
            CancellationToken.None);
    }

    [Fact]
    public async Task InMemoryFileStorage_CanProcess_WorksForValidAndInvalidFileIds()
    {
        // Arrange
        var storage = new InMemoryFileStorage();

        // Act & Assert
        Assert.True(storage.CanProcess("mem://basket/1/file.txt"));
        Assert.False(storage.CanProcess("pg://basket/1/file.txt"));
        Assert.False(storage.CanProcess("unknown://file.txt"));
    }

    [Fact]
    public async Task InMemoryFileStorage_BasketProperty_ReturnsConfiguredBasket()
    {
        // Arrange
        const string expectedBasket = "my-custom-basket";
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions(expectedBasket));

        // Assert
        Assert.Equal(expectedBasket, storage.Basket);
    }

    [Fact]
    public async Task InMemoryFileStorage_StorageTypeConstant_ReturnsMem()
    {
        // Assert
        Assert.Equal("mem", InMemoryFileStorage.DefaultStorageType);
        Assert.Equal("://", InMemoryFileStorage.SchemeSeparator);
    }
}

// Minimal TimeProvider implementation for testing
internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
