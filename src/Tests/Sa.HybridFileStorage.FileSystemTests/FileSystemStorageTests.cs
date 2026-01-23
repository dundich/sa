using Sa.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

public class FileSystemStorageTests(FileSystemStorageTests.Fixture fixture)
    : IClassFixture<FileSystemStorageTests.Fixture>
{
    public sealed class Fixture : SaFixture<IFileStorage, FileSystemStorageOptions>
    {
        public Fixture()
            : base(new FileSystemStorageOptions
            {
                BasePath = "mytemp"
            })
        {
            SetupServices = (services, cfg)
                => services.AddFileSystemFileStorage(Settings);
        }

        public override ValueTask DisposeAsync()
        {
            Directory.Delete(Settings.BasePath, true);
            return base.DisposeAsync();
        }
    }

    private IFileStorage Storage => fixture.Sub;


    [Fact]
    public async Task Crud()
    {
        var metadata = new UploadFileInput { FileName = "test.bin", TenantId = 1 };
        using MemoryStream fileContent = FixtureHelper.GetByteStream();

        var result = await Storage.UploadAsync(metadata, fileContent, fixture.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);

        bool canProcessed = Storage.CanProcess(result.FileId);
        Assert.True(canProcessed);

        var isSame = await EnsureFileSame(result.FileId, fileContent);
        Assert.True(isSame);

        var isDeleted = await Storage.DeleteAsync(result.FileId, fixture.CancellationToken);

        Assert.True(isDeleted);

        isSame = await EnsureFileSame(result.FileId, fileContent);

        Assert.False(isSame);
    }

    private async Task<bool> EnsureFileSame(string fileName, MemoryStream expectedBytes)
    {
        expectedBytes.Seek(0, SeekOrigin.Begin);

        using MemoryStream memoryStream = new();

        var isDownloaded = await Storage.DownloadAsync(fileName
            , (stream, ct) => stream.CopyToAsync(memoryStream, ct)
            , fixture.CancellationToken);

        if (isDownloaded)
        {
            Assert.Equal(expectedBytes.ToArray(), memoryStream.ToArray());
        }

        return isDownloaded;
    }
}
