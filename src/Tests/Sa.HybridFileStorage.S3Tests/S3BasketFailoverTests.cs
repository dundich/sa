using Sa.Data.S3;
using Sa.Fixture;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

/// <summary>
/// End-to-end probing of a basket backed by two S3 buckets: the chain is
/// [bucket-a, bucket-b] in registration order, reads continue to bucket-b on a miss in
/// bucket-a (same storage type), and uploads are first-wins into bucket-a.
/// </summary>
public sealed class S3BasketFailoverTests(S3TwoBucketsFixture fixture) : IClassFixture<S3TwoBucketsFixture>
{
    private IHybridFileStorage Storage => fixture.Sub;
    private CancellationToken CancellationToken => fixture.CancellationToken;

    [Fact]
    public void Chain_ContainsBothStorages_OfTheSameBasket()
    {
        var storages = Storage.Storages.ToArray();

        Assert.Equal(2, storages.Length);
        Assert.All(storages, s => Assert.Equal("share", s.Basket));
        Assert.All(storages, s => Assert.True(s.CanProcess("s3://share/1/x.txt")));
    }

    [Fact]
    public async Task Upload_WritesToTheFirstBucketOfTheBasket()
    {
        var metadata = new UploadFileInput { FileName = "from-hybrid.txt", TenantId = 1 };
        using var fileContent = FixtureHelper.GetByteStream(1024);

        var result = await Storage.UploadAsync("share", metadata, fileContent, CancellationToken);

        Assert.StartsWith("s3://share/1/from-hybrid.txt", result.FileId, StringComparison.Ordinal);
        Assert.True(await IsInBucketAsync("bucket-a", result.FileId));
        Assert.False(await IsInBucketAsync("bucket-b", result.FileId));
    }

    [Fact]
    public async Task Download_ServesFromTheFirstBucket_WhenTheFileIsThere()
    {
        await EnsureBucketAsync("bucket-a");
        await DirectPutInBucketAsync("bucket-a", "share/1/first-only.txt");

        using var downloaded = new MemoryStream();
        var isDownloaded = await Storage.DownloadAsync(
            "s3://share/1/first-only.txt",
            (stream, ct) => stream.CopyToAsync(downloaded, ct),
            CancellationToken);

        Assert.True(isDownloaded);
        Assert.Equal(1024, downloaded.Length);
    }

    [Fact]
    public async Task Download_ProbesTheSecondBucket_WhenTheFirstBucketMisses()
    {
        // The file exists in bucket-b only; the first storage misses (the object is not in
        // bucket-a) and the chain — both storages are type "s3" — continues to bucket-b.
        await EnsureBucketAsync("bucket-a");
        await EnsureBucketAsync("bucket-b");
        await DirectPutInBucketAsync("bucket-b", "share/1/only-in-second.txt");

        using var downloaded = new MemoryStream();
        var isDownloaded = await Storage.DownloadAsync(
            "s3://share/1/only-in-second.txt",
            (stream, ct) => stream.CopyToAsync(downloaded, ct),
            CancellationToken);

        Assert.True(isDownloaded);
        Assert.Equal(1024, downloaded.Length);
    }

    private async Task EnsureBucketAsync(string bucket)
    {
        using var client = new S3BucketClient(new HttpClient(), fixture.CreateSettings(bucket));
        if (!await client.IsBucketExists(CancellationToken))
        {
            await client.CreateBucket(CancellationToken);
        }
    }

    private async Task DirectPutInBucketAsync(string bucket, string filePath)
    {
        using var client = new S3BucketClient(new HttpClient(), fixture.CreateSettings(bucket));
        using var data = FixtureHelper.GetByteStream(1024);
        var uploaded = await client.UploadFile(filePath, FixtureHelper.StreamContentType, data, CancellationToken);
        Assert.True(uploaded);
    }

    private async Task<bool> IsInBucketAsync(string bucket, string fileId)
    {
        using var client = new S3BucketClient(new HttpClient(), fixture.CreateSettings(bucket));
        var filePath = fileId[(fileId.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return await client.IsFileExists(filePath, CancellationToken);
    }
}