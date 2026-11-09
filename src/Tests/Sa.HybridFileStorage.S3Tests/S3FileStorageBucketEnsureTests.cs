using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

/// <summary>
/// Bucket initialization is retried, not cached forever: a transient S3 error or one cancelled
/// first upload must not poison every later upload (the previous lazy <c>Task</c> kept the faulted
/// result — and the first caller's cancellation token — for the lifetime of the storage).
/// </summary>
public sealed class S3FileStorageBucketEnsureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upload_RetriesBucketInitialization_AfterTransientFailure()
    {
        // Arrange — the first bucket check fails the way a transient S3 outage would.
        var client = new FakeS3BucketClient { FailFirstBucketCheck = true };
        var storage = new S3FileStorage(client, new S3FileStorageOptions());
        var input = new UploadFileInput { FileName = "x.txt", TenantId = 1 };

        // Act & Assert — the first upload surfaces the error...
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UploadAsync(input, new MemoryStream([1, 2, 3]), Token));

        // ...and the second one retries the initialization instead of replaying the failure.
        StorageResult result = await storage.UploadAsync(input, new MemoryStream([1, 2, 3]), Token);

        Assert.Equal("s3://share/1/x.txt", result.FileId);
        Assert.Equal(2, client.BucketChecks);
        Assert.True(client.BucketCreated);
    }

    [Fact]
    public async Task Upload_CancelledDuringBucketInitialization_RetriesOnTheNextUpload()
    {
        // Arrange — the first bucket check hangs until the caller cancels.
        var client = new FakeS3BucketClient { HangFirstBucketCheck = true };
        var storage = new S3FileStorage(client, new S3FileStorageOptions());
        var input = new UploadFileInput { FileName = "x.txt", TenantId = 1 };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<StorageResult> first = storage.UploadAsync(input, new MemoryStream([1, 2, 3]), cts.Token);

        // Act — the caller gives up on the initialization...
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // ...and the next upload is not stuck on the cancelled attempt.
        StorageResult result = await storage.UploadAsync(input, new MemoryStream([1, 2, 3]), Token);

        Assert.Equal("s3://share/1/x.txt", result.FileId);
        Assert.Equal(2, client.BucketChecks);
        Assert.True(client.BucketCreated);
    }

    /// <summary>
    /// An <see cref="IS3BucketClient"/> whose bucket lifecycle can be made to fail or hang once,
    /// so bucket initialization is observable without Docker. Object operations are not exercised
    /// by these tests and throw.
    /// </summary>
    private sealed class FakeS3BucketClient : IS3BucketClient
    {
        private int _bucketChecks;
        private volatile bool _bucketCreated;

        public bool FailFirstBucketCheck { get; init; }

        public bool HangFirstBucketCheck { get; init; }

        public bool BucketCreated => _bucketCreated;

        public int BucketChecks => Volatile.Read(ref _bucketChecks);

        public string Bucket => "mybucket";

        public Uri Endpoint => new("http://localhost:9000");

        public async Task<bool> IsBucketExists(CancellationToken ct)
        {
            int call = Interlocked.Increment(ref _bucketChecks);

            if (call == 1 && FailFirstBucketCheck)
            {
                throw new InvalidOperationException("S3 is unavailable.");
            }

            if (call == 1 && HangFirstBucketCheck)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return _bucketCreated;
        }

        public Task<bool> CreateBucket(CancellationToken ct)
        {
            _bucketCreated = true;
            return Task.FromResult(true);
        }

        public Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken ct)
            => Task.FromResult(true);

        public string BuildFileUrl(string fileName) => $"http://localhost:9000/mybucket/{fileName}";

        public Task<bool> DeleteBucket(CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> DeleteBucket(bool forceDelete, CancellationToken ct) => throw new NotSupportedException();

        public string BuildFileUrl(string fileName, TimeSpan expiration) => throw new NotSupportedException();

        public Task DeleteFile(string fileName, CancellationToken ct) => throw new NotSupportedException();

        public Task<S3File> GetFile(string fileName, CancellationToken ct) => throw new NotSupportedException();

        public Task<Stream> GetFileStream(string fileName, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> IsFileExists(string fileName, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<string> List(string? prefix, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
