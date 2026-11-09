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
    public async Task NotThrowIfForceDeleteNotExistsBucket()
    {
        // force-путь начинается с листинга, который на отсутствующем бакете отвечает 404. Контракт
        // должен совпадать с обычным удалением: false, а не HttpRequestException.
        using var client = CloneClient("force-absent");

        Assert.False(await client.DeleteBucket(forceDelete: true, CancellationToken));
    }

    [Fact]
    public async Task ForceDeleteBucketWithObjects()
    {
        using var client = CloneClient("force-small");

        Assert.True(await client.CreateBucket(CancellationToken));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(await client.UploadFile($"f{i}.bin", "application/octet-stream", [1], CancellationToken));
        }

        Assert.True(await client.DeleteBucket(forceDelete: true, CancellationToken));
        Assert.False(await client.IsBucketExists(CancellationToken));
    }

    [Fact]
    public async Task ListAndForceDeleteMoreThanOneThousandObjects()
    {
        // Две регрессии сразу: ListObjectsV2 отдаёт не больше 1000 ключей за ответ (раньше вторая
        // страница молча пропадала), а очистка удаляла по одному объекту за запрос.
        const int count = 1005;
        using var client = CloneClient("force-many");

        Assert.True(await client.CreateBucket(CancellationToken));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (index, token) =>
                Assert.True(await client.UploadFile($"k{index:D4}.bin", "application/octet-stream", [1], token)));

        var listed = 0;
        await foreach (var _ in client.List(null, CancellationToken))
        {
            listed++;
        }

        Assert.Equal(count, listed);

        Assert.True(await client.DeleteBucket(forceDelete: true, CancellationToken));
        Assert.False(await client.IsBucketExists(CancellationToken));
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
