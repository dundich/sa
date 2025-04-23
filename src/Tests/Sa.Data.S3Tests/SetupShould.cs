using Sa.Data.S3;
using Sa.Data.S3.Fixture;

namespace Sa.Data.S3Tests;


public class SetupShould(SetupShould.Fixture fixture) : IClassFixture<SetupShould.Fixture>
{
    private IS3BucketClient Client => fixture.Sub;
    private CancellationToken CancellationToken => fixture.CancellationToken;

    public class Fixture : S3Fixture<IS3BucketClient>
    {
        public Fixture()
            : base()
        {
            SetupServices = (services, cfg) => services.AddS3BucketClientAsSingleton(CreateSettings("mybucket"));
        }

        public async override ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            await Sub.CreateBucket(CancellationToken.None);
        }
    }


    [Fact]
    public async Task CrudStream()
    {
        var fileName = ObjectShould.GetRandomFileName();
        var data = ObjectShould.GetByteStream(1500);
        var filePutResult = await Client.UploadFile(fileName, ObjectShould.StreamContentType, data, CancellationToken);

        Assert.True(filePutResult);

        await EnsureFileSame(fileName, data);
        await Client.DeleteFile(fileName, CancellationToken);
    }


    private async Task EnsureFileSame(string fileName, MemoryStream expectedBytes)
    {
        expectedBytes.Seek(0, SeekOrigin.Begin);

        using var getFileResult = await Client.GetFile(fileName, CancellationToken);

        using var memoryStream = GetEmptyByteStream(getFileResult.Length);
        var stream = await getFileResult.GetStream(CancellationToken);
        await stream.CopyToAsync(memoryStream, CancellationToken);


        Assert.Equal(expectedBytes.ToArray(), memoryStream.ToArray());
    }

    public static MemoryStream GetEmptyByteStream(long? size = null)
    {
        return size.HasValue
            ? new MemoryStream(new byte[(int)size])
            : new MemoryStream();
    }
}
