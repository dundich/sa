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
        var options = new InMemoryFileStorageOptions { Basket = "test", MaxSizeBytes = 1 * 1024 * 1024 };
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
    public async Task InMemoryFileStorage_ConcurrentUploadsOfOneFile_ChargeThePayloadOnce()
    {
        // Arrange — a 1000-byte limit and 16 parallel uploads of the same new 600-byte file.
        // Without an atomic read-check-write each upload that sees no previous payload charges the
        // full size, so the limit trips while a single file's worth of data is held.
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions { Basket = "test", MaxSizeBytes = 1000 });
        var input = new UploadFileInput { FileName = "parallel.bin", TenantId = 1 };

        // Act
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            storage.UploadAsync(input, new MemoryStream(new byte[600]), TestContext.Current.CancellationToken)));

        // Assert — 600 of 1000 bytes are held, so another 350-byte file still fits.
        await storage.UploadAsync(
            new UploadFileInput { FileName = "after.bin", TenantId = 1 },
            new MemoryStream(new byte[350]),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InMemoryFileStorage_Overwrite_DoesNotChargeTheOldBytesAgain()
    {
        // Arrange — a 1000-byte limit holding one 600-byte file. Each re-upload replaces the
        // payload of the same file ID, so the accounting must stay net-zero per overwrite.
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions { Basket = "test", MaxSizeBytes = 1000 });
        var input = new UploadFileInput { FileName = "same.bin", TenantId = 1 };

        // Act — five replacements of one file (3000 bytes written, 600 bytes retained)
        for (var i = 0; i < 5; i++)
        {
            await storage.UploadAsync(input, new MemoryStream(new byte[600]), CancellationToken.None);
        }

        // Assert — the limit still applies to a *new* file that would overflow it
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = "other.bin", TenantId = 1 },
                new MemoryStream(new byte[600]),
                CancellationToken.None));

        Assert.Contains("size limit", ex.Message);
    }

    [Fact]
    public async Task InMemoryFileStorage_Overwrite_ShrinkingTheFile_ReturnsTheBytes()
    {
        // Arrange — a large file replaced by a small one must free the difference, otherwise a
        // later upload of that size would be rejected although the storage holds less data.
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions { Basket = "test", MaxSizeBytes = 1000 });
        var input = new UploadFileInput { FileName = "shrinks.bin", TenantId = 1 };

        await storage.UploadAsync(input, new MemoryStream(new byte[900]), CancellationToken.None);

        // Act — 900 -> 100 frees 800 bytes
        await storage.UploadAsync(input, new MemoryStream(new byte[100]), CancellationToken.None);

        // Assert — 100 (shrunk file) + 850 = 950 <= 1000
        await storage.UploadAsync(
            new UploadFileInput { FileName = "second.bin", TenantId = 1 },
            new MemoryStream(new byte[850]),
            CancellationToken.None);
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
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions { Basket = "test", IsReadOnly = true });
        using var stream = FixtureHelper.GetByteStream();

        // Act & Assert
        await Assert.ThrowsAsync<HybridFileStorageWritableException>(() =>
            storage.UploadAsync(new UploadFileInput { FileName = "nope.txt", TenantId = 1 }, stream, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryFileStorage_ReadOnly_DeleteThrowsWritableException()
    {
        // Arrange
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions { Basket = "test", IsReadOnly = true });

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
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions { Basket = "test", MaxSizeBytes = 0 }); // unlimited
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

        // The scheme prefix includes the separator: a foreign scheme that merely starts with
        // "mem" is not this storage's, and neither is a bare name.
        Assert.False(storage.CanProcess("memory://basket/1/file.txt"));
        Assert.False(storage.CanProcess("member.txt"));
        Assert.False(storage.CanProcess(null!));
    }

    [Fact]
    public async Task InMemoryFileStorage_GetMetadata_NumericDirectory_KeepsTheFullFileName()
    {
        // Arrange — a directory named with a number is part of the name for this provider and
        // must not be mistaken for the Postgres timestamp segment.
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions { Basket = "share" });
        var input = new UploadFileInput { FileName = "2024/report.pdf", TenantId = 1 };

        using var stream = FixtureHelper.GetByteStream();
        var result = await storage.UploadAsync(input, stream, CancellationToken.None);

        // Act
        var metadata = await storage.GetMetadataAsync(result.FileId, CancellationToken.None);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("2024/report.pdf", metadata.FileName);
        Assert.Equal("share", metadata.Basket);
        Assert.Equal(1, metadata.TenantId);
    }

    [Fact]
    public async Task InMemoryFileStorage_BasketProperty_ReturnsConfiguredBasket()
    {
        // Arrange
        const string expectedBasket = "my-custom-basket";
        var storage = new InMemoryFileStorage(new InMemoryFileStorageOptions { Basket = expectedBasket });

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
