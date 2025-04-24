using Sa.Data.S3;
using Sa.Data.S3.Fixture;

namespace Sa.Data.S3Tests;

public class BucketShould(S3BucketClientFixture fixture) : IClassFixture<S3BucketClientFixture>
{
    protected IS3BucketClient Client => fixture.Sub;

    protected S3BucketClient CloneClient(string backetName) => fixture.CreateClient(backetName);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;


    [Fact]
    public async Task CreateBucket()
    {
        var bucket = "new";
        using var client = CloneClient(bucket);
        var bucketCreateResult = await client.CreateBucket(CancellationToken);
        Assert.True(bucketCreateResult);
    }


    [Fact]
    public async Task BeNotExists()
    {
        string bucket = "some";

        using var client = CloneClient(bucket);

        var bucketExistsResult = await client.IsBucketExists(CancellationToken);

        Assert.False(bucketExistsResult);
    }

    [Fact]
    public async Task NotThrowIfCreateBucketAlreadyExists()
    {
        string backet = "foo";

        using var client = CloneClient(backet);

        var bucketExistsResult = await client.CreateBucket(CancellationToken);

        Assert.True(bucketExistsResult);

        using var client2 = CloneClient(backet);

        bucketExistsResult = await client2.CreateBucket(CancellationToken);

        Assert.False(bucketExistsResult);
    }

    [Fact]
    public async Task NotThrowIfDeleteNotExistsBucket()
    {
        string backet = "bar";

        using var client = CloneClient(backet);

        var result = await client.DeleteBucket(CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteTestBucket()
    {
        string backet = "del";

        using var client = CloneClient(backet);
        var result = await client.CreateBucket(CancellationToken);
        Assert.True(result);

        var bucketDeleteResult = await client.DeleteBucket(CancellationToken);

        Assert.True(bucketDeleteResult);
    }
}
