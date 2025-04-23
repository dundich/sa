using Sa.Data.S3.Fixture;
using Sa.HybridFileStorage.Domain;
using Xunit;

namespace Sa.HybridFileStorage.S3Tests;

public class S3FileStorageTests(S3FileStorageFixture fixture) : IClassFixture<S3FileStorageFixture>
{
    private IFileStorage Storage => fixture.Sub;

    [Fact]
    public async Task Crud()
    {
        // Arrange
        var metadata = new UploadFileInput { FileName = "test.txt", TenantId = 1 };
        using MemoryStream fileContent = S3FixtureHelper.GetByteStream();

        // Act
        var result = await Storage.UploadFileAsync(metadata, fileContent, fixture.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.FileId);

        var isSame = await EnsureFileSame(result.FileId, fileContent);
        Assert.True(isSame);

        var isDeleted = await Storage.DeleteFileAsync(result.FileId, fixture.CancellationToken);

        Assert.True(isDeleted);

        isSame = await EnsureFileSame(result.FileId, fileContent);

        Assert.False(isSame);
    }

    private async Task<bool> EnsureFileSame(string fileName, MemoryStream expectedBytes)
    {
        expectedBytes.Seek(0, SeekOrigin.Begin);

        using MemoryStream memoryStream = new();

        var isDownloaded = await Storage.DownloadFileAsync(fileName
            , (stream, ct) => stream.CopyToAsync(memoryStream, ct)
            , fixture.CancellationToken);

        if (isDownloaded)
        {
            Assert.Equal(expectedBytes.ToArray(), memoryStream.ToArray());
        }

        return isDownloaded;
    }
}
