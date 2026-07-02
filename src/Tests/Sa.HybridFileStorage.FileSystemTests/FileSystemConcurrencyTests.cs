using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

public sealed class FileSystemConcurrencyTests : IAsyncLifetime
{
    private readonly string _testDir = $"concurrency_{Path.GetRandomFileName()}";
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
    public async Task FileSystemStorage_ConcurrentUploads_NoCorruption()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        const int uploadCount = 20;
        var tasks = new List<Task<StorageResult>>();

        // Act — concurrent uploads
        for (var i = 0; i < uploadCount; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                using var stream = FixtureHelper.GetByteStream(1024);
                return await storage.UploadAsync(
                    new UploadFileInput { FileName = $"file_{index}.txt", TenantId = 1 },
                    stream,
                    _cts.Token);
            }, _cts.Token));
        }

        var results = await Task.WhenAll(tasks);

        // Assert — all should succeed with unique file IDs
        Assert.Equal(uploadCount, results.Length);
        var uniqueIds = new HashSet<string>(results.Select(r => r.FileId));
        Assert.Equal(uploadCount, uniqueIds.Count);

        // Verify each file can be downloaded
        foreach (var result in results)
        {
            var downloaded = await storage.DownloadAsync(result.FileId, (_, _) => Task.CompletedTask, _cts.Token);
            Assert.True(downloaded);
        }
    }

    [Fact]
    public async Task FileSystemStorage_ConcurrentDeletes_NoException()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Upload files first
        var fileIds = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            using var stream = FixtureHelper.GetByteStream();
            var result = await storage.UploadAsync(
                new UploadFileInput { FileName = $"delete_me_{i}.txt", TenantId = 1 },
                stream,
                _cts.Token);
            fileIds.Add(result.FileId);
        }

        // Act — concurrent deletes
        var deleteTasks = fileIds.Select(id =>
            storage.DeleteAsync(id, _cts.Token)).ToList();

        var results = await Task.WhenAll(deleteTasks);

        // Assert — all should succeed
        Assert.All(results, r => Assert.True(r));
    }

    [Fact]
    public async Task FileSystemStorage_MixedConcurrentOps_NoRaceCondition()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSaFileSystemFileStorage(new FileSystemStorageSettings { BasePath = _testDir });

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        var operations = new List<Task>();

        // Mixed: uploads, downloads, deletes happening concurrently
        for (var i = 0; i < 15; i++)
        {
            var index = i;
            operations.Add(Task.Run(async () =>
            {
                using var stream = FixtureHelper.GetByteStream(512);
                var result = await storage.UploadAsync(
                    new UploadFileInput { FileName = $"mixed_{index}.txt", TenantId = 1 },
                    stream,
                    _cts.Token);

                // Immediately download
                var downloaded = await storage.DownloadAsync(result.FileId, (_, _) => Task.CompletedTask, _cts.Token);
                Assert.True(downloaded);

                // Then delete
                var deleted = await storage.DeleteAsync(result.FileId, _cts.Token);
                Assert.True(deleted);
            }, _cts.Token));
        }

        // Act
        await Task.WhenAll(operations);

        // Assert — no exceptions thrown
    }
}
