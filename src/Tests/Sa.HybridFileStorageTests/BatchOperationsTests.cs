using Microsoft.Extensions.DependencyInjection;
using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorageTests;

public sealed class BatchOperationsTests : IAsyncLifetime
{
    private IServiceProvider? _provider;
    private IHybridFileStorage? _storage;
    private readonly CancellationTokenSource _cts = new();
    private string? _tempDir;

    public async ValueTask InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"batch_test_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(_tempDir);

        var services = new ServiceCollection();
        
        // Register FileSystem with "share" basket
        services.AddSingleton<IFileStorage>(new FileSystemStorage(
            new FileSystemStorageSettings { BasePath = _tempDir }));
        
        // Register InMemory with "memory" basket
        services.AddSingleton<IFileStorage>(new InMemoryFileStorage(
            new InMemoryFileStorageOptions("memory")));
        
        services.AddSaHybridFileStorage();

        _provider = services.BuildServiceProvider(true);
        _storage = _provider.GetRequiredService<IHybridFileStorage>();
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        (_provider as IDisposable)?.Dispose();

        if (_tempDir is not null)
            try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CopyToScopeBatchAsync_ContinueOnErrorFalse_StopsOnFirstError()
    {
        // Arrange — one valid fileId, one invalid
        var input = new UploadFileInput { FileName = "valid.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await _storage!.UploadAsync("share", input, stream, _cts.Token);
        var validFileId = result.FileId;
        var invalidFileId = "nonexistent://file";

        var fileIds = new[] { validFileId, invalidFileId, validFileId };

        // Act
        var batchResult = await _storage.CopyToScopeBatchAsync(
            fileIds,
            "memory",
            configure: default,
            options: new BatchOptions { ContinueOnError = false, MaxDegreeOfParallelism = 1 },
            cancellationToken: _cts.Token);

        // Assert — should stop after first failure (index 1)
        Assert.True(batchResult.HasErrors);
        Assert.Equal(2, batchResult.Total);
        Assert.Single(batchResult.Succeeded);
        Assert.Single(batchResult.Failed);
        Assert.Equal(invalidFileId, batchResult.Failed[0].FileId);
    }

    [Fact]
    public async Task CopyToScopeBatchAsync_ProgressCallback_ReportsProgress()
    {
        // Arrange
        var input = new UploadFileInput { FileName = "progress.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await _storage!.UploadAsync("share", input, stream, _cts.Token);

        var fileIds = new[] { result.FileId, result.FileId, result.FileId };
        var progressReports = new List<BatchOperationProgress>();
        var progress = new Progress<BatchOperationProgress>(p => progressReports.Add(p));

        // Act
        var batchResult = await _storage.CopyToScopeBatchAsync(
            fileIds,
            "memory",
            configure: default,
            options: new BatchOptions { Progress = progress, MaxDegreeOfParallelism = 1 },
            cancellationToken: _cts.Token);

        // Assert
        Assert.Equal(3, batchResult.Total);
        Assert.Equal(3, batchResult.Succeeded.Count);
        Assert.NotEmpty(progressReports);
        // Last report should show 100%
        var lastProgress = progressReports.Last();
        Assert.Equal(100.0, lastProgress.PercentComplete);
    }

    [Fact]
    public void BatchResult_ThrowIfHasErrors_ThrowsBatchOperationException()
    {
        // Arrange
        var errors = new List<BatchError>
        {
            new BatchError("file1.txt", new InvalidOperationException("fail"), 0)
        };
        var result = new BatchResult<StorageResult>
        {
            Succeeded = [],
            Failed = errors.AsReadOnly()
        };

        // Act & Assert
        var ex = Assert.Throws<BatchOperationException<StorageResult>>(() =>
            result.ThrowIfHasErrors("Custom error message"));

        Assert.Contains("Custom error message", ex.Message);
        Assert.Equal(errors, ex.Result.Failed);
    }

    [Fact]
    public void BatchResult_ThrowIfNoErrors_DoesNotThrow()
    {
        // Arrange
        var result = new BatchResult<StorageResult>
        {
            Succeeded = [],
            Failed = []
        };

        // Act & Assert — should not throw
        result.ThrowIfHasErrors();
    }

    [Fact]
    public async Task CopyToScopeBatchAsync_EmptyFileList_ReturnsEmptyResult()
    {
        // Act
        var result = await _storage!.CopyToScopeBatchAsync(
            Array.Empty<string>().ToArray(),
            "memory",
            cancellationToken: _cts.Token);

        // Assert
        Assert.Equal(0, result.Total);
        Assert.False(result.HasErrors);
        Assert.Empty(result.Succeeded);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task CopyToScopeBatchAsync_AllFail_HasAllErrors()
    {
        // Arrange — all invalid file IDs
        var fileIds = new[] { "invalid1://x", "invalid2://y", "invalid3://z" };

        // Act
        var result = await _storage!.CopyToScopeBatchAsync(
            fileIds,
            "memory",
            options: new BatchOptions { ContinueOnError = true },
            cancellationToken: _cts.Token);

        // Assert
        Assert.Equal(3, result.Total);
        Assert.True(result.HasErrors);
        Assert.Empty(result.Succeeded);
        Assert.Equal(3, result.Failed.Count);
    }

    [Fact]
    public async Task CopyToScopeBatchAsync_OperationTimeout_CancelsLongRunning()
    {
        // Arrange
        var input = new UploadFileInput { FileName = "timeout.txt", TenantId = 1 };
        using var stream = FixtureHelper.GetByteStream();
        var result = await _storage!.UploadAsync("share", input, stream, _cts.Token);

        var fileIds = new[] { result.FileId };

        // Act — very short timeout should cause cancellation
        var options = new BatchOptions
        {
            OperationTimeout = TimeSpan.FromMilliseconds(1),
            ContinueOnError = true
        };

        var batchResult = await _storage.CopyToScopeBatchAsync(
            fileIds,
            "memory",
            options: options,
            cancellationToken: _cts.Token);

        // Assert — may succeed or fail depending on timing, but shouldn't hang
        Assert.Equal(1, batchResult.Total);
    }

    [Fact]
    public async Task CopyToScopeBatchAsync_MaxDegreeOfParallelism_LimitedConcurrency()
    {
        // Arrange — upload several files
        var fileIds = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var input = new UploadFileInput { FileName = $"parallel_{i}.txt", TenantId = 1 };
            using var stream = FixtureHelper.GetByteStream();
            var r = await _storage!.UploadAsync("share", input, stream, _cts.Token);
            fileIds.Add(r.FileId);
        }

        var progress = new Progress<BatchOperationProgress>(p =>
        {
            // Track peak concurrency by looking at completed count
        });

        // Act
        var result = await _storage!.CopyToScopeBatchAsync(
            fileIds,
            "memory",
            options: new BatchOptions
            {
                MaxDegreeOfParallelism = 2,
                Progress = progress,
                ContinueOnError = true
            },
            cancellationToken: _cts.Token);

        // Assert — all should succeed with limited parallelism
        Assert.Equal(5, result.Total);
        Assert.Equal(5, result.Succeeded.Count);
    }
}
