using Sa.Data.S3;
using Sa.Data.S3.Fixture;
using Sa.Fixture;

namespace Sa.Data.S3Tests;


public class SetupBucketClientShould(S3BucketClientFixtureDI fixture) : IClassFixture<S3BucketClientFixtureDI>
{
    private IS3BucketClient Client => fixture.Sub;
    private CancellationToken CancellationToken => fixture.CancellationToken;


    [Fact]
    public async Task Crud()
    {
        var fileName = ObjectShould.GetRandomFileName();
        using var data = ObjectShould.GetByteStream(1500);
        var filePutResult = await Client.UploadFile(fileName, ObjectShould.StreamContentType, data, CancellationToken);

        Assert.True(filePutResult);

        await EnsureFileSame(fileName, data);
        await Client.DeleteFile(fileName, CancellationToken);
    }


    private async Task EnsureFileSame(string fileName, MemoryStream expectedBytes)
    {
        expectedBytes.Seek(0, SeekOrigin.Begin);

        using var getFileResult = await Client.GetFile(fileName, CancellationToken);

        using var memoryStream = FixtureHelper.GetEmptyByteStream(getFileResult.Length);
        var stream = await getFileResult.GetStream(CancellationToken);
        await stream.CopyToAsync(memoryStream, CancellationToken);


        Assert.Equal(expectedBytes.ToArray(), memoryStream.ToArray());
    }
}
